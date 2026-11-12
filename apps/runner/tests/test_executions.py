import base64
import hashlib
import sqlite3
import time
import uuid
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from ringfour_runner.artifacts import open_regular_under
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


def test_artifact_snapshot_persists_and_reads_bounded_chunks(tmp_path):
    settings, body = setup(tmp_path, "import os,pathlib\np=pathlib.Path(os.environ['RINGFOUR_ARTIFACT_DIR'])/'sub'\np.mkdir()\n(p/'report.txt').write_text('hello')\n")
    headers = {"authorization": "Bearer test-secret"}
    execution_id = body["execution_id"]
    with TestClient(create_app(settings)) as client:
        assert client.post("/v1/executions", json=body, headers=headers).status_code == 201
        assert wait_terminal(client, execution_id, headers)["state"] == "succeeded"
        listing = client.get(f"/v1/executions/{execution_id}/artifacts", headers=headers).json()
        assert listing["artifacts"] == [{"path": "sub/report.txt", "size": 5, "sha256": "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"}]
        params = {"path": "sub/report.txt", "offset": 1, "limit": 2}
        chunk = client.get(f"/v1/executions/{execution_id}/artifacts/content", params=params, headers=headers).json()
        assert base64.b64decode(chunk["data_base64"]) == b"el"
        assert chunk["next_offset"] == 3 and not chunk["eof"]
        assert client.get(f"/v1/executions/{execution_id}/artifacts/content", params={**params, "path": "../secret"}, headers=headers).status_code == 400
        assert client.get(f"/v1/executions/{execution_id}/artifacts", headers={}).status_code == 401
    with TestClient(create_app(settings)) as client:
        assert client.get(f"/v1/executions/{execution_id}/artifacts", headers=headers).json() == listing
        assert client.get(f"/v1/executions/{execution_id}/artifacts/content", params={**params, "offset": 5}, headers=headers).json()["eof"]


def test_invalid_artifacts_fail_without_exposing_partial_snapshot(tmp_path):
    settings, body = setup(tmp_path, "import os,pathlib\np=pathlib.Path(os.environ['RINGFOUR_ARTIFACT_DIR'])\n(p/'good').write_text('ok')\n(p/'bad').symlink_to('/etc/passwd')\n")
    headers = {"authorization": "Bearer test-secret"}
    with TestClient(create_app(settings)) as client:
        assert client.post("/v1/executions", json=body, headers=headers).status_code == 201
        final = wait_terminal(client, body["execution_id"], headers)
        assert final["state"] == "failed" and final["error"]["code"] == "artifact_collection_failed"
        listing = client.get(f"/v1/executions/{body['execution_id']}/artifacts", headers=headers).json()
        assert listing["artifacts"] == []


def test_artifact_limits_and_not_terminal_response(tmp_path):
    settings, body = setup(tmp_path, "import os,pathlib,time\np=pathlib.Path(os.environ['RINGFOUR_ARTIFACT_DIR'])\n(p/'too-big').write_bytes(b'x' * 262145)\ntime.sleep(0.5)\n")
    headers = {"authorization": "Bearer test-secret"}
    with TestClient(create_app(settings)) as client:
        assert client.post("/v1/executions", json=body, headers=headers).status_code == 201
        response = client.get(f"/v1/executions/{body['execution_id']}/artifacts", headers=headers)
        assert response.status_code == 409
        assert response.json()["error"]["code"] == "execution_not_terminal"
        final = wait_terminal(client, body["execution_id"], headers)
        assert final["state"] == "failed" and final["error"]["code"] == "artifact_collection_failed"
        assert client.get(f"/v1/executions/{body['execution_id']}/artifacts", headers=headers).json()["artifacts"] == []


def test_artifact_open_refuses_symlinked_parent(tmp_path):
    root = tmp_path / "root"
    outside = tmp_path / "outside"
    root.mkdir()
    outside.mkdir()
    (outside / "secret").write_text("private")
    (root / "link").symlink_to(outside, target_is_directory=True)
    with pytest.raises(OSError):
        open_regular_under(root, "link/secret")


def test_script_lock_and_python_requirement(tmp_path):
    settings, body = setup(tmp_path, "print('locked')\n")
    lock = Path(settings.stage_dir) / "test" / "requirements.lock"
    lock.write_bytes(b"")
    _, digest = bundle_files(settings, "test", "main.py")
    body = {**body, "revision_digest": digest, "python_requirement": ">=3.13,<3.14", "lock_sha256": hashlib.sha256(b"").hexdigest()}
    headers = {"authorization": "Bearer test-secret"}
    with TestClient(create_app(settings)) as client:
        assert client.post("/v1/executions", json={**body, "python_requirement": ">=99"}, headers=headers).status_code == 400
        assert client.post("/v1/executions", json={**body, "lock_sha256": "0" * 64}, headers=headers).status_code == 400
        assert client.post("/v1/executions", json=body, headers=headers).status_code == 201
        result = wait_terminal(client, body["execution_id"], headers)
        assert result["state"] == "succeeded" and result["exit_code"] == 0


def test_execution_retention_capacity_refuses_new_work(tmp_path):
    settings, body = setup(tmp_path, "print('hello')\n")
    with TestClient(create_app(settings)) as client:
        with sqlite3.connect(Path(settings.data_dir) / "executions.db") as conn:
            for _ in range(100):
                conn.execute("INSERT INTO executions(execution_id,robot_id,request,revision_digest,state,created_at,updated_at) VALUES (?, ?, '{}', ?, 'succeeded', '2026-01-01', '2026-01-01')",
                    (str(uuid.uuid4()), body["robot_id"], body["revision_digest"]))
        response = client.post("/v1/executions", json=body, headers={"authorization": "Bearer test-secret"})
        assert response.status_code == 503 and response.json()["error"]["code"] == "capacity_exceeded"
