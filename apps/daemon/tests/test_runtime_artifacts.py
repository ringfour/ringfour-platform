import hashlib
import hmac
import asyncio
import sqlite3
import uuid

import httpx
from fastapi.testclient import TestClient

from ringfour_daemon import runtime_artifacts
from ringfour_daemon.config import Settings
from ringfour_daemon.db import migrate
from ringfour_daemon.main import create_app


def test_publication_snapshots_then_durably_queues(tmp_path, monkeypatch):
    db = str(tmp_path / "daemon.db")
    secret_path = tmp_path / "credential"
    secret_path.write_text("device-secret")
    migrate(db)
    robot, execution, artifact_id = (str(uuid.uuid4()) for _ in range(3))
    with sqlite3.connect(db) as conn:
        conn.execute("INSERT INTO device_identity VALUES (1,?,?,?,?)", (robot, str(uuid.uuid4()), "http://platform", str(uuid.uuid4())))
        conn.execute("INSERT INTO device_operations VALUES (?,?,?,1,'{}','running','2099-01-01','2026-01-01',NULL)", (execution, robot, "script.execution.start"))
    settings = Settings(db_path=db, runner_url="http://runner", runner_auth_token="runner-secret", credential_path=str(secret_path))
    metadata = {"artifact_id": artifact_id, "path": "photo.png", "size": 5,
                "sha256": hashlib.sha256(b"photo").hexdigest(), "content_type": "image/png", "state": "pending"}
    original = httpx.AsyncClient
    def transport(request):
        if request.method == "POST" and request.url.path.endswith("/live-artifacts"):
            return httpx.Response(200, json=metadata)
        return httpx.Response(503)
    monkeypatch.setattr(runtime_artifacts.httpx, "AsyncClient", lambda **kw: original(transport=httpx.MockTransport(transport), **kw))
    mac = hmac.new(b"runner-secret", ("ringfour-logbook-v1:" + execution).encode(), hashlib.sha256).hexdigest()
    headers = {"Authorization": f"Bearer {execution}:{mac}"}
    body = {"artifact_id": artifact_id, "path": "photo.png", "content_type": "image/png"}
    with TestClient(create_app(settings)) as client:
        response = client.post("/v1/runtime/artifacts", headers=headers, json=body)
        assert response.status_code == 200 and response.json() == metadata
        assert client.post("/v1/runtime/artifacts", headers=headers, json=body).status_code == 200
    with sqlite3.connect(db) as conn:
        assert conn.execute("SELECT artifact_id,execution_id,path,size,sha256,content_type FROM live_artifact_queue").fetchone() == \
            (artifact_id, execution, "photo.png", 5, metadata["sha256"], "image/png")


def test_uploaded_checkpoint_retries_cleanup_without_reupload(tmp_path, monkeypatch):
    db = str(tmp_path / "daemon.db")
    credential = tmp_path / "credential"
    credential.write_text("secret")
    migrate(db)
    artifact, execution = str(uuid.uuid4()), str(uuid.uuid4())
    with sqlite3.connect(db) as conn:
        conn.execute("INSERT INTO live_artifact_queue VALUES (?,?,?,?,?,?,?,?)",
            (artifact, execution, "photo.png", 5, "0" * 64, "image/png", "uploaded", 1.0))
    calls = []
    original = httpx.AsyncClient
    def transport(request):
        calls.append((request.method, request.url.host, request.url.path))
        return httpx.Response(204)
    monkeypatch.setattr(runtime_artifacts.httpx, "AsyncClient", lambda **kw: original(transport=httpx.MockTransport(transport), **kw))
    settings = Settings(db_path=db, runner_url="http://runner", runner_auth_token="runner-secret", credential_path=str(credential))
    identity = {"platform_url": "http://platform", "credential_id": str(uuid.uuid4())}
    row = (artifact, execution, "photo.png", 5, "0" * 64, "image/png", "uploaded")
    asyncio.run(runtime_artifacts.deliver_one(settings, identity, row))
    assert calls == [("POST", "runner", f"/v1/executions/{execution}/live-artifacts/{artifact}/ack")]
    with sqlite3.connect(db) as conn:
        assert conn.execute("SELECT count(*) FROM live_artifact_queue").fetchone()[0] == 0
