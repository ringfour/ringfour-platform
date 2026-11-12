import base64
import sqlite3
import time
import uuid
from pathlib import Path

from fastapi.testclient import TestClient

from ringfour_runner.config import Settings
from ringfour_runner.executions import bundle_files
from ringfour_runner.main import create_app


def setup(tmp_path, source):
    stage = tmp_path / "stage"
    bundle = stage / "test"
    bundle.mkdir(parents=True)
    (bundle / "main.py").write_text(source)
    data = tmp_path / "data"
    settings = Settings(str(data), str(stage), "test-secret")
    _, digest = bundle_files(settings, "test", "main.py")
    return settings, {"execution_id": str(uuid.uuid4()), "robot_id": str(uuid.uuid4()), "revision_digest": digest, "code_ref": "test", "entrypoint": "main.py"}


def wait_terminal(client, execution_id, headers):
    for _ in range(200):
        value = client.get(f"/v1/executions/{execution_id}", headers=headers).json()
        if value["state"] in {"succeeded", "failed", "cancelled"}:
            return value
        time.sleep(0.05)
    raise AssertionError("execution did not complete")


def test_auth_start_replay_and_output(tmp_path):
    settings, body = setup(tmp_path, "print('hello')\n")
    headers = {"authorization": "Bearer test-secret"}
    with TestClient(create_app(settings)) as client:
        assert client.post("/v1/executions", json=body).status_code == 401
        assert client.get(f"/v1/executions/{body['execution_id']}", headers={"authorization": "Bearer incorrect"}).status_code == 401
        assert client.post("/v1/executions", content=b"x" * 4097, headers=headers).status_code == 413
        start = client.post("/v1/executions", json=body, headers=headers)
        assert start.status_code == 201
        assert client.post("/v1/executions", json=body, headers=headers).status_code == 200
        assert client.post("/v1/executions", json={**body, "entrypoint": "other.py"}, headers=headers).status_code == 409
        final = wait_terminal(client, body["execution_id"], headers)
        assert final["state"] == "succeeded" and final["exit_code"] == 0
        output = client.get(f"/v1/executions/{body['execution_id']}/output", params={"stream": "stdout", "offset": 0, "limit": 100}, headers=headers).json()
        assert base64.b64decode(output["data_base64"]) == b"hello\n"
        assert output["eof"] and output["next_offset"] == 6
        invalid = client.get(f"/v1/executions/{body['execution_id']}/output", params={"stream": "stdout", "offset": "bad", "limit": 1}, headers=headers)
        assert invalid.status_code == 400 and invalid.json()["error"]["code"] == "invalid_input"
        invalid = client.get(f"/v1/executions/{body['execution_id']}/output", params={"stream": "stdout", "offset": 65537, "limit": 1}, headers=headers)
        assert invalid.status_code == 400
        with sqlite3.connect(Path(settings.data_dir) / "executions.db") as conn:
            events = conn.execute("SELECT robot_id, operation_id, event_type, schema_version FROM events ORDER BY sequence").fetchall()
            assert conn.execute("SELECT version FROM schema_migrations").fetchall() == [(1,)]
        assert [event[2] for event in events] == ["execution.queued", "execution.preparing", "execution.running", "execution.succeeded"]
        assert all(event[0] == body["robot_id"] and event[1] == body["execution_id"] and event[3] == 1 for event in events)


def test_traversal_and_digest_rejected(tmp_path):
    settings, body = setup(tmp_path, "print('hello')\n")
    headers = {"authorization": "Bearer test-secret"}
    with TestClient(create_app(settings)) as client:
        assert client.post("/v1/executions", json={**body, "code_ref": "../test"}, headers=headers).status_code == 400
        assert client.post("/v1/executions", json={**body, "revision_digest": "0" * 64}, headers=headers).status_code == 400
        (Path(settings.stage_dir) / "test" / "link").symlink_to("main.py")
        assert client.post("/v1/executions", json=body, headers=headers).status_code == 400


def test_overlap_stop_and_output_cap(tmp_path):
    settings, body = setup(tmp_path, "import signal,time\nsignal.signal(signal.SIGTERM, signal.SIG_IGN)\nprint('x'*70000, flush=True)\ntime.sleep(60)\n")
    headers = {"authorization": "Bearer test-secret"}
    with TestClient(create_app(settings)) as client:
        assert client.post("/v1/executions", json=body, headers=headers).status_code == 201
        for _ in range(100):
            item = client.get(f"/v1/executions/{body['execution_id']}", headers=headers).json()
            out = client.get(f"/v1/executions/{body['execution_id']}/output", params={"stream": "stdout", "offset": 0, "limit": 16384}, headers=headers).json()
            if item["state"] == "running" and out["truncated"]:
                break
            time.sleep(0.05)
        assert out["truncated"]
        assert client.post("/v1/executions", json={**body, "execution_id": str(uuid.uuid4())}, headers=headers).status_code == 409
        assert client.post(f"/v1/executions/{body['execution_id']}/stop", headers=headers).status_code == 200
        stopped = wait_terminal(client, body["execution_id"], headers)
        assert stopped["state"] == "cancelled" and stopped["exit_code"] == -9
        assert (Path(settings.data_dir) / "executions" / body["execution_id"] / "stdout").stat().st_size == 65536


def test_nonzero_exit_and_restart_reconciliation(tmp_path):
    settings, body = setup(tmp_path, "raise SystemExit(7)\n")
    headers = {"authorization": "Bearer test-secret"}
    with TestClient(create_app(settings)) as client:
        client.post("/v1/executions", json=body, headers=headers)
        final = wait_terminal(client, body["execution_id"], headers)
        assert final["state"] == "failed" and final["exit_code"] == 7
    with sqlite3.connect(Path(settings.data_dir) / "executions.db") as conn:
        conn.execute("UPDATE executions SET state='running' WHERE execution_id=?", (body["execution_id"],))
    with TestClient(create_app(settings)) as client:
        final = client.get(f"/v1/executions/{body['execution_id']}", headers=headers).json()
        assert final["state"] == "failed" and final["error"]["code"] == "interrupted_unknown_outcome"


def test_preparation_failure_is_explicit(tmp_path, monkeypatch):
    settings, body = setup(tmp_path, "print('hello')\n")
    headers = {"authorization": "Bearer test-secret"}
    monkeypatch.setenv("PATH", str(tmp_path))
    with TestClient(create_app(settings)) as client:
        assert client.post("/v1/executions", json=body, headers=headers).status_code == 201
        final = wait_terminal(client, body["execution_id"], headers)
        assert final["state"] == "failed"
        assert final["error"]["code"] == "preparation_failed"
