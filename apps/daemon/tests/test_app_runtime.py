import base64
import hashlib
import io
import json
import tarfile
import uuid
from dataclasses import replace

import httpx
import pytest
from fastapi.testclient import TestClient
from ringfour_daemon import (
    app_runtime,
    runtime_interactions,
    runtime_logbook,
    runtime_settings,
)
from ringfour_daemon.db import connect
from ringfour_daemon.main import create_app
from test_app_engine import setup as make_engine_setup


@pytest.fixture
def setup(tmp_path):
    yield from make_engine_setup.__wrapped__(tmp_path)


def test_run_credential_survives_replacement(setup):
    executor, _engine, run = setup
    token = app_runtime.credential(executor.settings, run)
    assert app_runtime.authenticate(executor.settings, "Bearer " + token) == run
    other = replace(executor.settings)
    assert app_runtime.credential(other, run) == token
    assert app_runtime.authenticate(other, "Bearer " + token + "tampered") is None
    runtime_settings = replace(other, app_runtime_url="http://daemon:8081")
    run_row = dict(executor.row(run)[0])
    environment = app_runtime.environment(runtime_settings, run_row)
    assert not any(item.startswith("RINGFOUR_SETTINGS_") for item in environment)
    run_row["snapshot_json"] = json.dumps({"runtime_settings": {"sample_interval_ms": 1000}})
    environment = app_runtime.environment(runtime_settings, run_row)
    assert "RINGFOUR_SETTINGS_URL=http://daemon:8081" in environment
    assert "RINGFOUR_SETTINGS_TOKEN=" + token in environment
    with connect(executor.settings.db_path) as conn:
        stored = conn.execute(
            "SELECT token_sha256 FROM app_run_credentials WHERE run_id=?", (run,)
        ).fetchone()[0]
        assert bytes(stored) == hashlib.sha256(token.encode()).digest()
    executor.start(run)
    executor.stop(run)
    assert app_runtime.authenticate(other, "Bearer " + token) is None


def test_settings_poll_result_is_run_scoped_durable_and_idempotent(setup, monkeypatch):
    executor, _engine, run = setup
    token = app_runtime.credential(executor.settings, run)
    executor.start(run)
    monkeypatch.setattr(app_runtime, "running", lambda *_: True)
    app_id, revision_id = str(uuid.uuid4()), str(uuid.uuid4())
    with connect(executor.settings.db_path) as conn, conn:
        conn.execute(
            "INSERT INTO app_settings_applications(application_id,run_id,installation_id,settings_revision_id,values_json,initial) VALUES (?,?,?,?,?,1)",
            (app_id, run, "install-1", revision_id, '{"period_ms":250}'),
        )
    app = create_app(executor.settings)
    client = TestClient(app)
    headers = {"Authorization": "Bearer " + token}
    pending = client.get("/v1/runtime/settings/pending", headers=headers)
    assert pending.status_code == 200
    assert pending.json()["pending"] == {
        "application_id": app_id,
        "settings_revision_id": revision_id,
        "values": {"period_ms": 250},
        "initial": True,
    }
    result_url = f"/v1/runtime/settings/{app_id}/result"
    result = {"outcome": "applied"}
    assert client.post(result_url, json=result, headers=headers).status_code == 200
    assert client.post(result_url, json=result, headers=headers).status_code == 200
    conflict = client.post(result_url, json={"outcome": "rejected", "reason": "no"}, headers=headers)
    assert conflict.status_code == 409
    other_run_application = client.post(
        f"/v1/runtime/settings/{uuid.uuid4()}/result", json=result, headers=headers
    )
    assert other_run_application.status_code == 404
    assert client.get("/v1/runtime/settings/pending", headers=headers).json()["pending"] is None
    replacement = TestClient(create_app(executor.settings))
    assert replacement.get("/v1/runtime/settings/pending", headers=headers).json()["pending"] is None
    assert replacement.post(result_url, json=result, headers=headers).status_code == 200
    with connect(executor.settings.db_path) as conn:
        row = conn.execute(
            "SELECT outcome,reason FROM app_settings_applications WHERE application_id=?", (app_id,)
        ).fetchone()
    assert row == ("applied", None)


def test_settings_command_recovers_from_new_runtime_instance_without_cross_run_delivery(setup, monkeypatch):
    executor, _engine, run = setup
    from ringfour_daemon import operations

    identity = executor.identity
    installation = str(uuid.uuid4())
    application, revision = str(uuid.uuid4()), str(uuid.uuid4())
    original_token = app_runtime.credential(executor.settings, run)
    run_row, _ = executor.row(run)
    monkeypatch.setattr(app_runtime, "running", lambda *_: True)
    start_id = str(uuid.uuid4())
    command = {
        "operation_id": application, "kind": "app.settings.apply", "payload_version": 1,
        "deadline_at": "2026-11-24T10:00:00Z",
        "payload": {
            "deployment_namespace": identity["deployment_namespace"],
            "installation_id": installation, "run_id": run,
            "application_id": application, "settings_revision_id": revision,
            "values": {"period_ms": 500}, "initial": False,
        },
    }
    runtime_settings.accept_command(executor.settings.db_path, identity["robot_id"], command)
    runtime_settings.accept_command(executor.settings.db_path, identity["robot_id"], command)

    # The settings intent is durable even when the start command arrives later.
    operations.record_command(executor.settings.db_path, identity["robot_id"], {
        "operation_id": start_id, "kind": "app.start", "payload_version": 1,
        "deadline_at": "2026-11-24T10:00:00Z", "payload": {"run_id": run}
    })
    # A replacement runtime instance reads the durable command from the same
    # SQLite database after the original instance may have lost its ack.
    replacement = TestClient(create_app(executor.settings))
    recovered = replacement.get(
        "/v1/runtime/settings/pending",
        headers={"Authorization": "Bearer " + original_token},
    )
    assert recovered.status_code == 200
    assert recovered.json()["pending"] == {
        "application_id": application,
        "settings_revision_id": revision,
        "values": {"period_ms": 500},
        "initial": False,
    }
    result_url = f"/v1/runtime/settings/{application}/result"
    result_headers = {"Authorization": "Bearer " + original_token}
    assert replacement.post(result_url, json={"outcome": "applied"}, headers=result_headers).status_code == 200
    # Retry the same result after another runtime instance replaces the one
    # that may have lost the response.
    replacement = TestClient(create_app(executor.settings))
    assert replacement.post(result_url, json={"outcome": "applied"}, headers=result_headers).status_code == 200

    # Once the original run ends, its unresolved/replayed command is not exposed
    # to the next run on that installation.
    later_run, later_operation = str(uuid.uuid4()), str(uuid.uuid4())
    operations.record_command(executor.settings.db_path, identity["robot_id"], {
        "operation_id": later_operation, "kind": "app.start", "payload_version": 1,
        "deadline_at": "2099-01-01T00:00:00Z", "payload": {"run_id": later_run}
    })
    with connect(executor.settings.db_path) as conn, conn:
        conn.execute("UPDATE app_runs SET state='stopped' WHERE run_id=?", (run,))
        conn.execute(
            "INSERT INTO app_runs(run_id,installation_id,robot_id,operation_id,app_id,revision_id,image_digest,configuration_revision_id,snapshot_json,state,requested_at,updated_at) VALUES (?,?,?,?,?,?,?,?,?,?,?,?)",
            (later_run, run_row["installation_id"], run_row["robot_id"], later_operation,
             run_row["app_id"], run_row["revision_id"], run_row["image_digest"],
             run_row["configuration_revision_id"], "{}", "accepted", "2026-11-24T10:01:00Z", "2026-11-24T10:01:00Z"),
        )
    later_run_token = app_runtime.credential(executor.settings, later_run)
    assert replacement.get(
        "/v1/runtime/settings/pending",
        headers={"Authorization": "Bearer " + later_run_token},
    ).json()["pending"] is None

    with connect(executor.settings.db_path) as conn:
        row = conn.execute(
            "SELECT application_id,run_id,settings_revision_id,outcome FROM app_settings_applications"
        ).fetchone()
    assert row == (application, run, revision, "applied")


def test_app_logbook_uses_run_authority_and_unknown_engine_cannot_accept_entries(
    setup, monkeypatch
):
    executor, _engine, run = setup
    token = app_runtime.credential(executor.settings, run)
    executor.start(run)
    app = create_app(executor.settings)
    client = TestClient(app)
    body = {
        "entry_id": str(uuid.uuid4()),
        "level": "info",
        "message": "App entry",
        "fields": {},
    }
    monkeypatch.setattr(app_runtime, "running", lambda *_: None)
    assert (
        client.post(
            "/v1/runtime/logbook/entries",
            json=body,
            headers={"Authorization": "Bearer " + token},
        ).status_code
        == 503
    )
    monkeypatch.setattr(app_runtime, "running", lambda *_: True)
    response = client.post(
        "/v1/runtime/logbook/entries",
        json=body,
        headers={"Authorization": "Bearer " + token},
    )
    assert response.status_code == 200, response.text
    with connect(executor.settings.db_path) as conn:
        event = json.loads(
            conn.execute("SELECT event_json FROM logbook_outbox").fetchone()[0]
        )
        assert event["execution_id"] == run
        assert token not in json.dumps(event)
    assert (
        runtime_logbook._operation(executor.settings.db_path, str(uuid.uuid4()), run)
        is None
    )


def test_artifact_publication_freezes_bytes_and_refuses_links_and_escape(
    setup, monkeypatch
):
    executor, engine, run = setup
    executor.start(run)
    data = {"value": b"immutable proof", "link": False}

    def archive(request):
        raw = io.BytesIO()
        with tarfile.open(fileobj=raw, mode="w") as tar:
            member = tarfile.TarInfo("proof.bin")
            if data["link"]:
                member.type = tarfile.SYMTYPE
                member.linkname = "/etc/passwd"
                tar.addfile(member)
            else:
                member.size = len(data["value"])
                tar.addfile(member, io.BytesIO(data["value"]))
        return httpx.Response(
            200,
            content=raw.getvalue(),
            headers={
                "X-Docker-Container-Path-Stat": base64.b64encode(
                    json.dumps({"size": len(data["value"]), "linkTarget": ""}).encode()
                ).decode()
            },
        )

    engine.client = httpx.Client(
        base_url="http://engine", transport=httpx.MockTransport(archive)
    )
    original = engine.request
    monkeypatch.setattr(
        engine,
        "request",
        lambda method, path, **kw: (
            archive(httpx.Request(method, "http://engine" + path))
            if method == "HEAD"
            else original(method, path, **kw)
        ),
    )
    value = {
        "artifact_id": str(uuid.uuid4()),
        "path": "proof.bin",
        "content_type": "application/octet-stream",
    }
    result = app_runtime.snapshot(executor.settings, executor, run, value)
    # Enrollment may happen after HTTP startup; the manager owns the current executor.
    from types import SimpleNamespace
    app = create_app(executor.settings)
    app.state.app_executor = None
    app.state.connection_manager = SimpleNamespace(app_executor=executor)
    monkeypatch.setattr(app_runtime, "running", lambda *_: True)
    token = app_runtime.credential(executor.settings, run)
    published = TestClient(app).post("/v1/runtime/artifacts", json=value, headers={"Authorization": "Bearer " + token})
    assert published.status_code == 200 and published.json() == result
    data["value"] = b"changed"
    assert app_runtime.snapshot(executor.settings, executor, run, value) == result
    assert (
        app_runtime.spool_root(executor.settings) / value["artifact_id"]
    ).read_bytes() == b"immutable proof"
    for path in ("../escape", "/etc/passwd", "dir/../escape", "dir//file"):
        with pytest.raises(ValueError):
            app_runtime.snapshot(
                executor.settings,
                executor,
                run,
                value | {"artifact_id": str(uuid.uuid4()), "path": path},
            )
    data["link"] = True
    with pytest.raises(ValueError):
        app_runtime.snapshot(
            executor.settings,
            executor,
            run,
            value | {"artifact_id": str(uuid.uuid4()), "path": "link"},
        )
    engine.client.close()


def test_answer_cannot_apply_after_local_App_stop(setup):
    executor, _engine, run = setup
    token = app_runtime.credential(executor.settings, run)
    executor.start(run)
    request = str(uuid.uuid4())
    with connect(executor.settings.db_path) as conn:
        conn.execute(
            "INSERT INTO runtime_interactions VALUES (?,?,?,?,?,?,NULL,NULL,'pending',NULL,NULL,1,1)",
            (
                request,
                run,
                executor.identity["robot_id"],
                "hash",
                "Continue?",
                json.dumps(
                    [{"value": "yes", "label": "Yes"}, {"value": "no", "label": "No"}]
                ),
            ),
        )
    executor.stop(run)
    runtime_interactions._apply_response(
        executor.settings.db_path,
        request,
        {
            "response_id": str(uuid.uuid4()),
            "chosen_value": "yes",
            "actor_user_id": str(uuid.uuid4()),
            "submitted_at": "2026-11-24T00:00:00Z",
        },
    )
    with connect(executor.settings.db_path) as conn:
        assert conn.execute(
            "SELECT state,response_json FROM runtime_interactions WHERE request_id=?",
            (request,),
        ).fetchone() == ("cancelled", None)
    assert app_runtime.authenticate(executor.settings, "Bearer " + token) is None


def test_truncation_is_delivered_when_saved_prefix_length_does_not_change(
    setup, monkeypatch
):
    import asyncio

    executor, _engine, run = setup
    executor.start(run)
    with connect(executor.settings.db_path) as conn:
        conn.execute("UPDATE app_runs SET stdout_truncated=0 WHERE run_id=?", (run,))
    sent = []

    def receive(request):
        sent.append(request)
        return httpx.Response(204)

    client = httpx.AsyncClient
    monkeypatch.setattr(
        app_runtime.httpx,
        "AsyncClient",
        lambda **kw: client(transport=httpx.MockTransport(receive), **kw),
    )
    monkeypatch.setattr(app_runtime, "machine_headers", lambda *_: {})
    asyncio.run(app_runtime.deliver_once(executor.settings, executor.identity))
    sent.clear()
    with connect(executor.settings.db_path) as conn:
        conn.execute("UPDATE app_runs SET stdout_truncated=1 WHERE run_id=?", (run,))
    asyncio.run(app_runtime.deliver_once(executor.settings, executor.identity))
    assert len(sent) == 1
    assert sent[0].url.params["truncated"] == "true" and sent[0].content == b""
    assert sent[0].url.params["offset"] == "65536"
