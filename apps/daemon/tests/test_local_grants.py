import base64
import hashlib
import json
import sqlite3
import uuid
from datetime import UTC, datetime, timedelta

import pytest
from fastapi.testclient import TestClient

from ringfour_daemon.config import Settings
from ringfour_daemon.db import migrate
from ringfour_daemon.local_control import create_local_app
from ringfour_daemon.local_grants import (
    GrantRejected,
    authorize,
    install,
    record_session,
)


def setup(tmp_path):
    db = str(tmp_path / "daemon.db")
    migrate(db)
    now = datetime.now(UTC)
    def ts(seconds):
        return (now + timedelta(seconds=seconds)).isoformat().replace("+00:00", "Z")
    session = str(uuid.uuid4())
    record_session(db, session, ts(3600), "create")
    raw = b"x" * 32
    token = base64.urlsafe_b64encode(raw).decode().rstrip("=")
    body = {"session_id": session, "grant_id": str(uuid.uuid4()), "operation_id": str(uuid.uuid4()),
            "token_sha256": hashlib.sha256(raw).hexdigest(), "expires_at": ts(120), "scopes": ["source", "output"]}
    return db, now, ts, token, body


def test_grants_are_hash_only_scoped_expiring_and_revoked(tmp_path):
    db, now, ts, token, body = setup(tmp_path)
    assert not authorize(db, body["session_id"], token, now=now)
    assert install(db, body, now=now) == {"grant_id": body["grant_id"]}
    assert install(db, body, now=now) == {"grant_id": body["grant_id"]}
    assert authorize(db, body["session_id"], token, now=now)
    assert not authorize(db, str(uuid.uuid4()), token, now=now)
    assert not authorize(db, body["session_id"], "bad", now=now)
    assert not authorize(db, body["session_id"], token, now=now + timedelta(seconds=121))
    with pytest.raises(GrantRejected):
        install(db, {**body, "token_sha256": "a" * 64}, now=now)
    with pytest.raises(GrantRejected):
        install(db, {**body, "operation_id": str(uuid.uuid4()), "grant_id": str(uuid.uuid4()), "expires_at": ts(301)}, now=now)
    with sqlite3.connect(db) as conn:
        dump = " ".join(conn.iterdump())
    assert token not in dump and base64.urlsafe_b64decode(token + "=").hex() not in dump
    replacement = {**body, "operation_id": str(uuid.uuid4()), "grant_id": str(uuid.uuid4()),
                   "token_sha256": hashlib.sha256(b"y" * 32).hexdigest()}
    install(db, replacement, now=now)
    assert not authorize(db, body["session_id"], token, now=now)
    assert install(db, body, now=now) == {"grant_id": body["grant_id"]}
    assert not authorize(db, body["session_id"], token, now=now)
    record_session(db, body["session_id"], ts(3600), "close")
    assert not authorize(db, body["session_id"], base64.urlsafe_b64encode(b"y" * 32).decode().rstrip("="), now=now)
    with pytest.raises(GrantRejected):
        record_session(db, body["session_id"], ts(3600), "create")


def test_local_listener_denies_missing_and_wrong_grants(tmp_path):
    db, now, _, token, body = setup(tmp_path)
    install(db, body, now=now)
    app = create_local_app(Settings(db_path=db, runner_url="http://unused"))
    url = f"/v1/local/dev-sessions/{body['session_id']}"
    with TestClient(app) as client:
        assert client.get(url).status_code == 401
        assert client.get(url, headers={"authorization": "Bearer wrong"}).status_code == 401
        result = client.get(url, headers={"authorization": f"Bearer {token}"})
        assert result.status_code == 200 and result.json()["session_id"] == body["session_id"]
        assert client.get("/docs").status_code == 404
        assert token not in json.dumps(result.json())
