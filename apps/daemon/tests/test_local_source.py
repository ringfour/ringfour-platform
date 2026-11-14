import base64
import hashlib
import io
import json
import sqlite3
import uuid
import zipfile
from datetime import UTC, datetime, timedelta

import httpx
from fastapi.testclient import TestClient

from ringfour_daemon import local_source
from ringfour_daemon.config import Settings
from ringfour_daemon.db import migrate
from ringfour_daemon.local_control import create_local_app
from ringfour_daemon.local_grants import install, record_session


def setup(tmp_path):
    settings = Settings(db_path=str(tmp_path / "db"), runner_url="http://runner", stage_dir=str(tmp_path / "stage"), runner_auth_token="secret")
    migrate(settings.db_path)
    now = datetime.now(UTC)
    session = str(uuid.uuid4())
    expiry = lambda seconds: (now + timedelta(seconds=seconds)).isoformat().replace("+00:00", "Z")
    record_session(settings.db_path, session, expiry(3600), "create")
    secret = b"x" * 32
    token = base64.urlsafe_b64encode(secret).decode().rstrip("=")
    grant = {"session_id": session, "grant_id": str(uuid.uuid4()), "operation_id": str(uuid.uuid4()),
             "token_sha256": hashlib.sha256(secret).hexdigest(), "expires_at": expiry(120), "scopes": ["source", "output"]}
    install(settings.db_path, grant, now=now)
    files = {"main.py": b"print('hello')\n", "requirements.lock": b""}
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as archive:
        for name, data in files.items(): archive.writestr(name, data)
    raw = buf.getvalue()
    h = lambda b: hashlib.sha256(b).hexdigest()
    body = {"base_generation": 0, "archive_sha256": h(raw), "archive_size": len(raw), "archive_base64": base64.b64encode(raw).decode(),
            "manifest": {"entrypoint": "main.py", "python_requirement": ">=3.13,<3.14", "lock_sha256": h(b""),
                         "files": [{"path": name, "size": len(data), "sha256": h(data)} for name, data in files.items()], "deleted_paths": []}}
    return settings, session, token, body


def test_local_upload_recovers_runner_failure_and_replays(tmp_path, monkeypatch):
    settings, session, token, body = setup(tmp_path)
    calls = []
    real_client = httpx.AsyncClient

    def handler(request):
        calls.append(request)
        if len(calls) == 1: return httpx.Response(503)
        request_body = json.loads(request.content)
        return httpx.Response(200, json={"session_id": session, "generation": 1, "archive_sha256": request_body["archive_sha256"]})

    monkeypatch.setattr(local_source.httpx, "AsyncClient", lambda **kwargs: real_client(transport=httpx.MockTransport(handler), **kwargs))
    headers = {"authorization": f"Bearer {token}", "Idempotency-Key": "test-sync"}
    with TestClient(create_local_app(settings)) as client:
        url = f"/v1/local/dev-sessions/{session}/source"
        assert client.get(url).status_code == 401
        assert client.get(url, headers=headers).json() == {"generation": 0, "paths": [], "archive_sha256": None}
        assert client.put(url, json=body).status_code == 401
        assert client.put(url, json=body, headers=headers).status_code == 503
        with sqlite3.connect(settings.db_path) as conn:
            first = conn.execute("SELECT operation_id,state FROM local_source_syncs").fetchone()
        assert first[1] == "pending"
        result = client.put(url, json=body, headers=headers)
        assert result.status_code == 200 and result.json()["generation"] == 1
        assert client.get(url, headers=headers).json() == {"generation": 1, "paths": ["main.py", "requirements.lock"], "archive_sha256": body["archive_sha256"]}
        assert client.put(url, json=body, headers=headers).json() == result.json()
        changed = {**body, "base_generation": 1}
        assert client.put(url, json=changed, headers=headers).status_code == 409
        assert client.put(url, json=body, headers={**headers, "Idempotency-Key": "different"}).status_code == 409
    assert len(calls) == 2
    assert json.loads(calls[0].content)["operation_id"] == json.loads(calls[1].content)["operation_id"] == first[0]
    assert b"archive_base64" not in calls[0].content


def test_invalid_source_is_rejected_before_staging(tmp_path):
    settings, session, token, body = setup(tmp_path)
    headers = {"authorization": f"Bearer {token}", "Idempotency-Key": "test-sync"}
    body["manifest"]["files"][0]["path"] = "../secret"
    with TestClient(create_local_app(settings)) as client:
        assert client.put(f"/v1/local/dev-sessions/{session}/source", json=body, headers=headers).status_code == 400
        assert client.put(f"/v1/local/dev-sessions/{session}/source", json={**body, "archive_size": 1048577}, headers=headers).status_code == 413
    with sqlite3.connect(settings.db_path) as conn:
        assert conn.execute("SELECT count(*) FROM local_source_syncs").fetchone()[0] == 0


def test_metadata_report_retries_without_sending_source(tmp_path, monkeypatch):
    import asyncio

    settings, session, _, body = setup(tmp_path)
    with sqlite3.connect(settings.db_path) as conn:
        grant_id = conn.execute("SELECT grant_id FROM local_dev_grants WHERE session_id=?", (session,)).fetchone()[0]
    operation = local_source.prepare(settings, session, grant_id, "report-test", body)
    requests = []
    real_client = httpx.AsyncClient

    def handler(request):
        requests.append(request)
        if request.url.host == "runner":
            return httpx.Response(200, json={"session_id": session, "generation": 1, "archive_sha256": body["archive_sha256"]})
        return httpx.Response(503 if len(requests) == 2 else 204)

    monkeypatch.setattr(local_source.httpx, "AsyncClient", lambda **kwargs: real_client(transport=httpx.MockTransport(handler), **kwargs))
    monkeypatch.setattr(local_source, "load_identity", lambda _: {"platform_url": "http://platform"})
    monkeypatch.setattr(local_source, "credential_error", lambda _: None)
    monkeypatch.setattr(local_source, "machine_headers", lambda *_: {"x-ringfour-credential-id": "test"})
    assert asyncio.run(local_source.process(settings, operation))["generation"] == 1
    asyncio.run(local_source.report(settings, operation))
    with sqlite3.connect(settings.db_path) as conn:
        assert conn.execute("SELECT state FROM local_source_syncs").fetchone()[0] == "committed"
    asyncio.run(local_source.report(settings, operation))
    with sqlite3.connect(settings.db_path) as conn:
        assert conn.execute("SELECT state FROM local_source_syncs").fetchone()[0] == "reported"
    sent = json.loads(requests[-1].content)
    assert sent["generation"] == 1 and sent["grant_id"] == grant_id
    assert set(sent) == {"operation_id", "grant_id", "base_generation", "generation", "archive_sha256", "archive_size", "lock_sha256"}
    assert "main.py" not in requests[-1].content.decode() and body["archive_base64"] not in requests[-1].content.decode()


def test_orphan_staging_cleanup_preserves_pending_and_recent_files(tmp_path):
    import os
    import time
    from pathlib import Path

    settings, session, _, body = setup(tmp_path)
    with sqlite3.connect(settings.db_path) as conn:
        grant_id = conn.execute("SELECT grant_id FROM local_dev_grants WHERE session_id=?", (session,)).fetchone()[0]
    operation = local_source.prepare(settings, session, grant_id, "cleanup-test", body)
    directory = Path(settings.stage_dir) / "dev-sync" / session
    referenced = directory / f"{operation}.zip"
    orphan = directory / f"{uuid.uuid4()}.zip"
    recent = directory / f"{uuid.uuid4()}.zip"
    for item in (orphan, recent): item.write_bytes(b"temp")
    old = time.time() - 600
    os.utime(orphan, (old, old))
    os.utime(referenced, (old, old))
    local_source.clean_orphaned_staging(settings)
    assert referenced.exists() and recent.exists() and not orphan.exists()
