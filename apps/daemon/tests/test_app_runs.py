import json
import uuid

import pytest
from ringfour_daemon import app_runs, operations
from ringfour_daemon.db import connect
from test_app_engine import setup as make_engine_setup


@pytest.fixture
def setup(tmp_path):
    yield from make_engine_setup.__wrapped__(tmp_path)


def operation(executor, run, kind="start"):
    row, _ = executor.row(run)
    payload = {
        "app_protocol_version": 1,
        "deployment_namespace": executor.identity["deployment_namespace"],
        "installation_id": row["installation_id"],
    }
    if kind == "stop":
        payload["expected_run_id"] = run
        op, _ = operations.record_command(
            executor.settings.db_path,
            row["robot_id"],
            {
                "operation_id": str(uuid.uuid4()),
                "kind": "app.stop",
                "payload_version": 1,
                "payload": payload,
                "deadline_at": "2099-01-01T00:00:00Z",
            },
        )
        return op
    with connect(executor.settings.db_path) as conn:
        conn.execute(
            "UPDATE device_operations SET payload=? WHERE operation_id=?",
            (json.dumps(payload | {"run_id": run}), row["operation_id"]),
        )
    return operations.get(executor.settings.db_path, row["operation_id"])


def reports(executor):
    with connect(executor.settings.db_path) as conn:
        return [
            json.loads(r[0])
            for r in conn.execute(
                "SELECT body_json FROM app_observation_outbox WHERE route='observations' ORDER BY sequence"
            )
        ]


def test_stop_before_create_persists_terminal_run_and_never_launches(setup):
    executor, engine, run = setup
    operation(executor, run)
    stop = operation(executor, run, "stop")
    app_runs.stop(executor.settings, executor, stop)
    app_runs.publish(executor.settings, executor, run)
    r = reports(executor)[0]
    assert r["runs"][0]["state"] == "stopped"
    assert {o["kind"]: o["state"] for o in r["operations"]} == {
        "start": "failed",
        "stop": "succeeded",
    }
    assert not engine.calls
    with connect(executor.settings.db_path) as conn:
        assert not conn.execute("SELECT 1 FROM device_execution_slots").fetchone()
    app_runs.publish(executor.settings, executor, run)
    assert len(reports(executor)) == 1


def test_observed_start_and_exit_share_ordered_durable_reports(setup):
    executor, engine, run = setup
    operation(executor, run)
    app_runs.publish(executor.settings, executor, run)
    assert reports(executor)[0]["runs"][0]["state"] == "accepted"
    executor.start(run)
    app_runs.publish(executor.settings, executor, run)
    assert reports(executor)[1]["operations"][0]["phase"] == "observed_running"
    stop = operation(executor, run, "stop")
    app_runs.stop(executor.settings, executor, stop)
    app_runs.publish(executor.settings, executor, run)
    r = reports(executor)
    assert [v["report_sequence"] for v in r] == [1, 2, 3]
    assert r[-1]["runs"][0]["state"] == "stopped"
    calls = list(engine.calls)
    app_runs.stop(executor.settings, executor, stop)
    assert engine.calls == calls


def test_stale_stop_is_bound_to_its_installation(setup):
    executor, engine, run = setup
    op = operation(executor, run, "stop")
    op.payload["installation_id"] = str(uuid.uuid4())
    app_runs.stop(executor.settings, executor, op)
    assert executor.row(run)[0]["state"] == "accepted"
    assert not engine.calls


def test_failed_process_has_a_contract_valid_terminal_report(setup):
    executor, engine, run = setup
    operation(executor, run)
    row = executor.start(run)
    container = engine.container(row["container_id"])
    container["State"].update(
        Status="exited",
        Running=False,
        ExitCode=1,
        FinishedAt="2026-11-24T12:00:00.123456789Z",
    )
    executor.reconcile(run)
    app_runs.publish(executor.settings, executor, run)
    report = reports(executor)[0]
    assert report["runs"][0]["failure"]["code"] == "execution_failed"
    assert report["runs"][0]["ended_at"] == "2026-11-24T12:00:00.123456Z"
