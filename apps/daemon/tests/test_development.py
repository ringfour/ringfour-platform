import base64
import hashlib
import io
import json
import stat
import uuid
import zipfile
from datetime import UTC, datetime, timedelta
from types import SimpleNamespace

import pytest
from fastapi.testclient import TestClient

from ringfour_daemon import development as dev
from ringfour_daemon.config import Settings
from ringfour_daemon.db import connect, migrate


def archive(files):
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w") as z:
        for name, data in files.items():
            z.writestr(name, data)
    return out.getvalue()


@pytest.fixture
def setup(tmp_path):
    settings = Settings(str(tmp_path / "daemon.db"))
    migrate(settings.db_path)
    robot, namespace, session = [str(uuid.uuid4()) for _ in range(3)]
    raw = b"x" * 32
    token = base64.urlsafe_b64encode(raw).decode().rstrip("=")
    body = {
        "session_id": session,
        "expires_at": (datetime.now(UTC) + timedelta(hours=1)).isoformat(),
        "grant_hash": hashlib.sha256(raw).hexdigest(),
        "grant_expires_at": (datetime.now(UTC) + timedelta(minutes=5)).isoformat(),
    }
    with connect(settings.db_path) as c:
        c.execute(
            "INSERT INTO device_identity VALUES (1,?,?,?,?)",
            (robot, str(uuid.uuid4()), "http://platform", str(uuid.uuid4())),
        )
        c.execute(
            "INSERT INTO device_deployment_namespace VALUES (1,?,?)",
            (namespace, datetime.now(UTC).isoformat()),
        )
        c.execute(
            "INSERT INTO app_development_sessions VALUES (?,?,?,?,0)",
            (session, robot, namespace, json.dumps(body)),
        )
    return settings, session, token, body


def test_snapshot_is_atomic_idempotent_and_immutable_before_execution(setup):
    settings, session, _, _ = setup
    raw = archive({"main.py": b"print(1)"})
    digest = "sha256:" + hashlib.sha256(raw).hexdigest()
    op = str(uuid.uuid4())
    first = dev.commit(settings, session, op, 0, digest, raw)
    assert first == dev.commit(settings, session, op, 0, digest, raw)
    with pytest.raises(ValueError, match="stale_generation"):
        dev.commit(settings, session, str(uuid.uuid4()), 2, digest, raw)
    with pytest.raises(ValueError, match="pending"):
        dev.commit(settings, session, str(uuid.uuid4()), 1, digest, raw)
    snapshot = first | {"session_id": session, "expires_at": "2099-01-01T00:00:00Z"}
    assert dev.snapshot_files(settings, snapshot) == {"main.py": b"print(1)"}
    dev.source_path(settings, session, 1).write_bytes(b"changed")
    with pytest.raises(ValueError, match="changed"):
        dev.snapshot_files(settings, snapshot)


@pytest.mark.parametrize(
    "path",
    [
        "../main.py",
        "/main.py",
        ".env",
        "src/.env",
        "src/../main.py",
        "a//main.py",
        "__pycache__/x.py",
        "a\\b.py",
    ],
)
def test_source_rejects_unsafe_paths(path):
    with pytest.raises(ValueError):
        dev.source_files(archive({path: b"bad"}))


def test_source_rejects_duplicate_files_and_links():
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w") as z:
        entry = zipfile.ZipInfo("main.py")
        entry.external_attr = (stat.S_IFLNK | 0o777) << 16
        z.writestr(entry, b"/etc/passwd")
    with pytest.raises(ValueError):
        dev.source_files(out.getvalue())


def test_local_api_denies_ungranted_upload_and_preserves_generation(setup):
    settings, session, token, _body = setup
    raw = archive({"main.py": b"print(1)"})
    with TestClient(dev.create_local_app(settings, SimpleNamespace())) as client:
        path = f"/v1/local/app-development/{session}/source"
        assert client.get(path).status_code == 401
        headers = {
            "Authorization": "Bearer " + token,
            "Idempotency-Key": str(uuid.uuid4()),
            "X-Base-Generation": "0",
            "X-Archive-Sha256": "sha256:" + hashlib.sha256(raw).hexdigest(),
        }
        assert client.put(path, headers=headers, content=raw).json()["generation"] == 1
        assert client.put(path, headers=headers, content=raw).json()["generation"] == 1
        assert client.get(path, headers=headers).json()["report_pending"]
        with connect(settings.db_path) as c:
            c.execute("UPDATE app_development_sessions SET closed=1")
        assert client.get(path, headers=headers).status_code == 401


def test_grant_is_bound_to_current_namespace_and_expires(setup):
    settings, session, token, body = setup
    assert dev.authorize(settings, session, "Bearer " + token)
    body["grant_expires_at"] = "2000-01-01T00:00:00Z"
    with connect(settings.db_path) as c:
        c.execute("UPDATE app_development_sessions SET body=?", (json.dumps(body),))
    with pytest.raises(ValueError):
        dev.authorize(settings, session, "Bearer " + token)
    with connect(settings.db_path) as c:
        c.execute(
            "UPDATE app_development_sessions SET namespace=?", (str(uuid.uuid4()),)
        )
    with pytest.raises(ValueError, match="closed"):
        dev.session_body(settings, session)


def test_disabling_fastlane_closes_cached_grants(setup):
    import asyncio
    from contextlib import suppress

    settings, session, token, _body = setup

    async def exercise():
        task = asyncio.create_task(
            dev.run(settings, SimpleNamespace(app_executor=None))
        )
        try:
            await asyncio.sleep(0.05)
            with pytest.raises(ValueError, match="closed"):
                dev.authorize(settings, session, "Bearer " + token)
        finally:
            task.cancel()
            with suppress(asyncio.CancelledError):
                await task

    asyncio.run(exercise())
