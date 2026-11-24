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
from ringfour_daemon import app_runtime, runtime_interactions, runtime_logbook
from ringfour_daemon.db import connect
from ringfour_daemon.main import create_app
from test_app_engine import setup as make_engine_setup


@pytest.fixture
def setup(tmp_path):
    yield from make_engine_setup.__wrapped__(tmp_path)


def test_run_credential_survives_replacement_and_is_independent_of_runner_secret(setup):
    executor, _engine, run = setup
    token = app_runtime.credential(executor.settings, run)
    assert app_runtime.authenticate(executor.settings, "Bearer " + token) == run
    other = replace(executor.settings, runner_auth_token="different-runner-secret")
    assert app_runtime.credential(other, run) == token
    assert app_runtime.authenticate(other, "Bearer " + token + "tampered") is None
    with connect(executor.settings.db_path) as conn:
        stored = conn.execute(
            "SELECT token_sha256 FROM app_run_credentials WHERE run_id=?", (run,)
        ).fetchone()[0]
        assert bytes(stored) == hashlib.sha256(token.encode()).digest()
    executor.start(run)
    executor.stop(run)
    assert app_runtime.authenticate(other, "Bearer " + token) is None


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


def test_delayed_development_restart_cannot_launch_over_an_App(setup):
    from ringfour_daemon import local_execution, runner

    executor, _engine, _run = setup
    with pytest.raises(runner.RunnerRejected) as failure:
        local_execution.record(
            executor.settings.db_path,
            {
                "execution_id": str(uuid.uuid4()),
                "session_id": str(uuid.uuid4()),
                "operation_id": str(uuid.uuid4()),
            },
        )
    assert failure.value.code == "execution_conflict"


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
