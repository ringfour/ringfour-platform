import asyncio
import json
import threading
import uuid

import httpx
import pytest
from ringfour_daemon import app_installations, app_runs, operations
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


def test_stop_command_does_not_hold_executor_mutex_during_docker_wait(setup):
    executor, engine, run = setup
    executor.start(run)
    stop = operation(executor, run, "stop")
    stop_entered = threading.Event()
    finish_stop = threading.Event()
    original_request = engine.request

    def delayed_request(method, path, **kwargs):
        if path.endswith("/stop"):
            stop_entered.set()
            assert finish_stop.wait(2)
        return original_request(method, path, **kwargs)

    engine.request = delayed_request
    failures = []

    def apply_stop():
        try:
            app_runs.stop(executor.settings, executor, stop)
        except Exception as error:  # noqa: BLE001 -- surfaced and asserted in the test thread
            failures.append(error)

    worker = threading.Thread(target=apply_stop)
    worker.start()
    assert stop_entered.wait(2)
    acquired = executor.mutex.acquire(timeout=0.2)
    finish_stop.set()
    if acquired:
        executor.mutex.release()
    worker.join(2)
    assert acquired, "stop command held the executor mutex while Docker waited"
    assert not worker.is_alive()
    assert not failures
    assert executor.row(run)[0]["state"] == "stopped"


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


@pytest.mark.parametrize(("outcome", "state"), [("applied", "succeeded"), ("rejected", "failed")])
def test_settings_result_is_exact_run_bound_and_enters_durable_observation_outbox(setup, outcome, state):
    executor, _engine, run = setup
    row, _ = executor.row(run)
    operation_id, revision_id = str(uuid.uuid4()), str(uuid.uuid4())
    payload = {
        "app_protocol_version": 1,
        "deployment_namespace": executor.identity["deployment_namespace"],
        "installation_id": row["installation_id"],
        "run_id": run,
        "application_id": operation_id,
        "settings_revision_id": revision_id,
        "expected_applied_revision_id": None,
        "values": {"period_ms": 250},
        "initial": False,
    }
    operations.record_command(executor.settings.db_path, row["robot_id"], {
        "operation_id": operation_id, "kind": "app.settings.apply", "payload_version": 1,
        "payload": payload, "deadline_at": "2099-01-01T00:00:00Z",
    })
    with connect(executor.settings.db_path) as conn, conn:
        conn.execute(
            "INSERT INTO app_settings_applications(application_id,run_id,installation_id,settings_revision_id,values_json,initial,outcome,reason,result_recorded_at) VALUES (?,?,?,?,?,0,?,?,?)",
            (operation_id, run, row["installation_id"], revision_id, '{"period_ms":250}', outcome,
             None if outcome == "applied" else "App declined this value", "2026-11-24T10:00:00Z"),
        )
    app_runs.publish(executor.settings, executor, run)
    report = reports(executor)[0]
    item = next(o for o in report["operations"] if o["kind"] == "settings.apply")
    assert item["state"] == state
    assert item["settings_application"] == {
        "application_id": operation_id,
        "settings_revision_id": revision_id,
        "outcome": outcome,
        "reason": None if outcome == "applied" else "App declined this value",
    }
    from ringfour_daemon import app_validation
    app_validation.validate(report, "device", "observations")


def test_settings_observation_survives_api_outage_and_replays_same_receipt_after_replacement(setup):
    executor, _engine, run = setup
    row, _ = executor.row(run)
    operation_id, revision_id = str(uuid.uuid4()), str(uuid.uuid4())
    payload = {"run_id": run, "installation_id": row["installation_id"]}
    operations.record_command(executor.settings.db_path, row["robot_id"], {
        "operation_id": operation_id, "kind": "app.settings.apply", "payload_version": 1,
        "payload": payload, "deadline_at": "2099-01-01T00:00:00Z",
    })
    with connect(executor.settings.db_path) as conn, conn:
        conn.execute(
            "INSERT INTO app_settings_applications(application_id,run_id,installation_id,settings_revision_id,values_json,initial,outcome,result_recorded_at) VALUES (?,?,?,?,?,0,'applied','2026-11-24T10:00:00Z')",
            (operation_id, run, row["installation_id"], revision_id, '{"period_ms":250}'),
        )
    app_runs.publish(executor.settings, executor, run)
    original = reports(executor)[0]

    class Offline:
        async def put(self, *_args, **_kwargs):
            raise httpx.ConnectError("API unavailable")

    with pytest.raises(httpx.ConnectError):
        asyncio.run(app_installations._deliver(executor.settings, Offline()))
    with connect(executor.settings.db_path) as conn:
        pending = conn.execute(
            "SELECT body_json,acknowledged_at FROM app_observation_outbox WHERE report_id=?", (original["report_id"],)
        ).fetchone()
        assert pending[1] is None
        assert json.loads(pending[0]) == original
        assert conn.execute(
            "SELECT delivered_at FROM app_settings_applications WHERE application_id=?", (operation_id,)
        ).fetchone()[0] is None

    class Accepted:
        def __init__(self, report):
            self.text = json.dumps({"report_id": report["report_id"], "report_sequence": report["report_sequence"], "received_at": "2026-11-24T10:00:01Z"})

        def raise_for_status(self):
            return None

    class RecoveredApi:
        def __init__(self):
            self.replayed = []

        async def put(self, _path, *, json):
            self.replayed.append(json)
            return Accepted(json)

    replacement_api = RecoveredApi()
    asyncio.run(app_installations._deliver(executor.settings, replacement_api))
    assert replacement_api.replayed == [original]
    with connect(executor.settings.db_path) as conn:
        assert conn.execute(
            "SELECT acknowledged_at FROM app_observation_outbox WHERE report_id=?", (original["report_id"],)
        ).fetchone()[0] == "2026-11-24T10:00:01Z"
        assert conn.execute(
            "SELECT delivered_at FROM app_settings_applications WHERE application_id=?", (operation_id,)
        ).fetchone()[0] == "2026-11-24T10:00:01Z"


def test_wakeup_during_tick_is_not_lost(monkeypatch):
    from types import SimpleNamespace

    async def scenario():
        first = asyncio.Event()
        release = asyncio.Event()
        second = asyncio.Event()
        calls = 0
        manager = SimpleNamespace(app_executor=object(), connected=True, app_headers={}, app_work=asyncio.Event())

        async def tick(*_):
            nonlocal calls
            calls += 1
            if calls == 1:
                first.set()
                await release.wait()
            else:
                second.set()

        monkeypatch.setattr(app_runs, "tick", tick)
        worker = asyncio.create_task(app_runs.run(None, manager))
        try:
            await asyncio.wait_for(first.wait(), timeout=1)
            manager.app_work.set()  # A committed command arrives while another tick is in progress.
            release.set()
            await asyncio.wait_for(second.wait(), timeout=.2)  # Faster than the .5 s recovery timer.
        finally:
            worker.cancel()
            try:
                await worker
            except asyncio.CancelledError:
                pass

    asyncio.run(scenario())
