import hashlib
import io
import sqlite3
import uuid
import zipfile
from pathlib import Path

from fastapi.testclient import TestClient

from ringfour_runner.config import Settings
from ringfour_runner.main import create_app


def make_snapshot(settings, session, files, base=0, deleted=()):
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w") as archive:
        for name, data in files.items():
            archive.writestr(name, data)
    data = out.getvalue()
    name = str(uuid.uuid4()) + ".zip"
    stage = Path(settings.stage_dir)
    stage.mkdir(parents=True, exist_ok=True)
    (stage / name).write_bytes(data)
    h = lambda b: hashlib.sha256(b).hexdigest()
    return {"session_id": session, "operation_id": str(uuid.uuid4()), "base_generation": base,
        "generation": base + 1, "archive_sha256": h(data), "archive_size": len(data), "staged_archive": name,
        "manifest": {"entrypoint": "main.py", "python_requirement": ">=3.13,<3.14", "lock_sha256": h(files["requirements.lock"]),
        "files": [{"path": path, "size": len(value), "sha256": h(value)} for path, value in files.items()], "deleted_paths": list(deleted)}}


def test_generations_are_atomic_persistent_and_idempotent(tmp_path):
    settings = Settings(str(tmp_path / "data"), str(tmp_path / "stage"), "secret")
    headers = {"authorization": "Bearer secret"}
    session = str(uuid.uuid4())
    create = {"session_id": session, "operation_id": str(uuid.uuid4()), "reason": "create", "expires_at": "2099-01-01T00:00:00Z"}
    first = make_snapshot(settings, session, {"main.py": b"print(1)\n", "requirements.lock": b"", "old.py": b"x"})
    with TestClient(create_app(settings)) as client:
        assert client.post("/v1/dev-sessions/lifecycle", json=create, headers=headers).status_code == 200
        assert client.post("/v1/dev-sessions/sync", json=first).status_code == 401
        response = client.post("/v1/dev-sessions/sync", json=first, headers=headers)
        assert response.status_code == 200 and response.json()["generation"] == 1
        assert client.post("/v1/dev-sessions/sync", json=first, headers=headers).json() == response.json()
        assert client.post("/v1/dev-sessions/sync", json={**first, "operation_id": str(uuid.uuid4())}, headers=headers).status_code == 409
        bad = make_snapshot(settings, session, {"main.py": b"print(2)\n", "requirements.lock": b""}, base=1)
        assert client.post("/v1/dev-sessions/sync", json=bad, headers=headers).status_code == 400
        second = {**bad, "operation_id": str(uuid.uuid4()), "manifest": {**bad["manifest"], "deleted_paths": ["old.py"]}}
        assert client.post("/v1/dev-sessions/sync", json=second, headers=headers).status_code == 200
        with sqlite3.connect(Path(settings.data_dir) / "executions.db") as db:
            rows = db.execute("SELECT generation,directory FROM dev_source_generations ORDER BY generation").fetchall()
        assert [row[0] for row in rows] == [1, 2]
        workspace = Path(settings.data_dir) / "dev-sessions" / session
        assert (workspace / rows[0][1] / "old.py").read_bytes() == b"x"
        assert not (workspace / rows[1][1] / "old.py").exists()
    with TestClient(create_app(settings)) as client:
        assert client.post("/v1/dev-sessions/sync", json=second, headers=headers).status_code == 200
        close = {**create, "operation_id": str(uuid.uuid4()), "reason": "close"}
        assert client.post("/v1/dev-sessions/lifecycle", json=close, headers=headers).status_code == 200
        assert not (Path(settings.data_dir) / "dev-sessions" / session).exists()


def test_invalid_files_never_commit(tmp_path):
    settings = Settings(str(tmp_path / "data"), str(tmp_path / "stage"), "secret")
    headers = {"authorization": "Bearer secret"}
    session = str(uuid.uuid4())
    create = {"session_id": session, "operation_id": str(uuid.uuid4()), "reason": "create", "expires_at": "2099-01-01T00:00:00Z"}
    with TestClient(create_app(settings)) as client:
        assert client.post("/v1/dev-sessions/lifecycle", json=create, headers=headers).status_code == 200
        for filename in ("../escape", ".env", "dir/.git/config", "dir/__pycache__/file"):
            body = make_snapshot(settings, session, {"main.py": b"pass", "requirements.lock": b"", filename: b"secret"})
            assert client.post("/v1/dev-sessions/sync", json=body, headers=headers).status_code == 400
        body = make_snapshot(settings, session, {"main.py": b"pass", "requirements.lock": b""})
        body["manifest"]["files"][0]["sha256"] = "a" * 64
        assert client.post("/v1/dev-sessions/sync", json=body, headers=headers).status_code == 400
        assert client.post("/v1/dev-sessions/sync", json={**body, "staged_archive": "../escape"}, headers=headers).status_code == 400
        with sqlite3.connect(Path(settings.data_dir) / "executions.db") as db:
            assert db.execute("SELECT count(*) FROM dev_source_generations").fetchone()[0] == 0


def test_interrupted_staging_retries_without_publishing_partial_generation(tmp_path, monkeypatch):
    from ringfour_runner import dev_source

    settings = Settings(str(tmp_path / "data"), str(tmp_path / "stage"), "secret")
    headers = {"authorization": "Bearer secret"}
    session = str(uuid.uuid4())
    create = {"session_id": session, "operation_id": str(uuid.uuid4()), "reason": "create", "expires_at": "2099-01-01T00:00:00Z"}
    body = make_snapshot(settings, session, {"main.py": b"pass", "requirements.lock": b""})
    real_replace = dev_source.os.replace
    calls = []

    def interrupted(source, destination):
        calls.append(True)
        if len(calls) == 1:
            raise OSError("simulated interrupted commit")
        return real_replace(source, destination)

    with TestClient(create_app(settings)) as client:
        assert client.post("/v1/dev-sessions/lifecycle", json=create, headers=headers).status_code == 200
        monkeypatch.setattr(dev_source.os, "replace", interrupted)
        assert client.post("/v1/dev-sessions/sync", json=body, headers=headers).status_code == 503
        with sqlite3.connect(Path(settings.data_dir) / "executions.db") as db:
            assert db.execute("SELECT count(*) FROM dev_source_generations").fetchone()[0] == 0
        assert client.post("/v1/dev-sessions/sync", json=body, headers=headers).status_code == 200
        with sqlite3.connect(Path(settings.data_dir) / "executions.db") as db:
            assert db.execute("SELECT count(*) FROM dev_source_generations").fetchone()[0] == 1


def test_session_generation_limit_is_enforced(tmp_path):
    settings = Settings(str(tmp_path / "data"), str(tmp_path / "stage"), "secret")
    headers = {"authorization": "Bearer secret"}
    session = str(uuid.uuid4())
    create = {"session_id": session, "operation_id": str(uuid.uuid4()), "reason": "create", "expires_at": "2099-01-01T00:00:00Z"}
    with TestClient(create_app(settings)) as client:
        assert client.post("/v1/dev-sessions/lifecycle", json=create, headers=headers).status_code == 200
        for generation in range(20):
            body = make_snapshot(settings, session, {"main.py": b"pass", "requirements.lock": b""}, base=generation)
            assert client.post("/v1/dev-sessions/sync", json=body, headers=headers).status_code == 200
        over = make_snapshot(settings, session, {"main.py": b"pass", "requirements.lock": b""}, base=20)
        response = client.post("/v1/dev-sessions/sync", json=over, headers=headers)
        assert response.status_code == 409 and response.json()["error"]["code"] == "capacity_exceeded"
