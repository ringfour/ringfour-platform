import sqlite3
import uuid
from pathlib import Path

from fastapi.testclient import TestClient

from ringfour_runner.config import Settings
from ringfour_runner.main import create_app


def test_lifecycle_replay_restart_and_isolated_cleanup(tmp_path):
    settings = Settings(str(tmp_path / "data"), str(tmp_path / "staged"), "secret")
    auth = {"authorization": "Bearer secret"}
    create = {"session_id": str(uuid.uuid4()), "operation_id": str(uuid.uuid4()),
              "expires_at": "2027-01-01T00:00:00Z", "reason": "create"}
    close = {**create, "operation_id": str(uuid.uuid4()), "reason": "close"}
    url = "/v1/dev-sessions/lifecycle"
    with TestClient(create_app(settings)) as client:
        assert client.post(url, json=create).status_code == 401
        assert client.post(url, json={**create, "session_id": "../secret"}, headers=auth).status_code == 400
        result = client.post(url, json=create, headers=auth)
        assert result.status_code == 200 and result.json()["state"] == "ready"
        assert client.post(url, json=create, headers=auth).json() == result.json()
        assert client.post(url, json={**create, "expires_at": "2027-01-02T00:00:00Z"}, headers=auth).status_code == 409
        workspace = Path(settings.data_dir) / "dev-sessions" / create["session_id"]
        (workspace / "code.py").write_text("test")
        production = Path(settings.data_dir) / "prepared-envs" / "production"
        production.mkdir(parents=True)
        (production / "keep").write_text("keep")
    with TestClient(create_app(settings)) as client:
        assert (workspace / "code.py").read_text() == "test"
        assert client.post(url, json=close, headers=auth).json()["state"] == "closed"
        assert client.post(url, json=close, headers=auth).json()["state"] == "closed"
        assert not workspace.exists()
        assert (production / "keep").read_text() == "keep"
        assert client.post(url, json={**create, "operation_id": str(uuid.uuid4())}, headers=auth).status_code == 409


def test_interrupted_operation_can_resume_and_never_follows_symlinks(tmp_path):
    settings = Settings(str(tmp_path / "data"), str(tmp_path / "staged"), "secret")
    auth = {"authorization": "Bearer secret"}
    body = {"session_id": str(uuid.uuid4()), "operation_id": str(uuid.uuid4()),
            "expires_at": "2027-01-01T00:00:00Z", "reason": "create"}
    with TestClient(create_app(settings)) as client:
        root = Path(settings.data_dir) / "dev-sessions"
        root.mkdir()
        target = tmp_path / "unrelated"
        target.mkdir()
        (target / "keep").write_text("keep")
        (root / body["session_id"]).symlink_to(target)
        assert client.post("/v1/dev-sessions/lifecycle", json=body, headers=auth).status_code == 503
        with sqlite3.connect(Path(settings.data_dir) / "executions.db") as conn:
            assert conn.execute("SELECT state FROM dev_session_operations WHERE operation_id=?", (body["operation_id"],)).fetchone()[0] == "running"
        (root / body["session_id"]).unlink()
        assert client.post("/v1/dev-sessions/lifecycle", json=body, headers=auth).status_code == 200
        assert (target / "keep").read_text() == "keep"
