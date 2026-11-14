"""Durable local development grants: only hashes cross the Platform boundary."""
import base64
import binascii
import hashlib
import hmac
import json
import re
import sqlite3
import uuid
from datetime import UTC, datetime, timedelta

_HASH = re.compile(r"[0-9a-f]{64}\Z")
_TOKEN = re.compile(r"[A-Za-z0-9_-]{43}\Z")


class GrantRejected(ValueError):
    pass


def _id(value):
    try:
        return isinstance(value, str) and str(uuid.UUID(value)) == value
    except ValueError:
        return False


def _timestamp(value):
    try:
        if not isinstance(value, str) or not value.endswith("Z"):
            raise ValueError
        return datetime.fromisoformat(value)
    except ValueError:
        raise GrantRejected("Invalid expiry.") from None


def record_session(db_path, session_id, expires_at, reason):
    """Record the Runner-confirmed lifecycle, without reopening closed sessions."""
    if not _id(session_id) or reason not in {"create", "close", "expire"}:
        raise GrantRejected("Invalid lifecycle.")
    _timestamp(expires_at)
    with sqlite3.connect(db_path) as conn:
        conn.execute("BEGIN IMMEDIATE")
        row = conn.execute("SELECT expires_at,state FROM local_dev_sessions WHERE session_id=?", (session_id,)).fetchone()
        if row and row[0] != expires_at:
            raise GrantRejected("Session expiry does not match.")
        if reason == "create":
            if row and row[1] == "closed":
                raise GrantRejected("Session is closed.")
            if not row:
                conn.execute("INSERT INTO local_dev_sessions VALUES (?,?, 'open')", (session_id, expires_at))
        else:
            if not row:
                conn.execute("INSERT INTO local_dev_sessions VALUES (?,?, 'closed')", (session_id, expires_at))
            else:
                conn.execute("UPDATE local_dev_sessions SET state='closed' WHERE session_id=?", (session_id,))
            conn.execute("DELETE FROM local_dev_grants WHERE session_id=?", (session_id,))


def install(db_path, body, *, now=None):
    """Install or replay an authenticated control-plane grant operation."""
    now = now or datetime.now(UTC)
    keys = {"operation_id", "session_id", "grant_id", "token_sha256", "expires_at", "scopes"}
    if not isinstance(body, dict) or set(body) != keys or not all(_id(body[key]) for key in ("operation_id", "session_id", "grant_id")):
        raise GrantRejected("Invalid grant.")
    token_hash = body["token_sha256"]
    if (not isinstance(token_hash, str) or not _HASH.fullmatch(token_hash) or not isinstance(body["scopes"], list)
            or any(not isinstance(scope, str) for scope in body["scopes"])
            or sorted(body["scopes"]) != ["output", "source"]):
        raise GrantRejected("Invalid grant.")
    expires = _timestamp(body["expires_at"])
    if expires <= now or expires > now + timedelta(minutes=5):
        raise GrantRejected("Invalid expiry.")
    fingerprint = hashlib.sha256(json.dumps(body, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    with sqlite3.connect(db_path) as conn:
        conn.execute("BEGIN IMMEDIATE")
        old = conn.execute("SELECT fingerprint FROM local_dev_grant_operations WHERE operation_id=?", (body["operation_id"],)).fetchone()
        if old:
            if not hmac.compare_digest(old[0], fingerprint):
                raise GrantRejected("Conflicting grant operation.")
            return {"grant_id": body["grant_id"]}
        session = conn.execute("SELECT expires_at,state FROM local_dev_sessions WHERE session_id=?", (body["session_id"],)).fetchone()
        if not session or session[1] != "open" or expires > _timestamp(session[0]) or _timestamp(session[0]) <= now:
            raise GrantRejected("Session unavailable.")
        try:
            conn.execute("INSERT INTO local_dev_grant_operations VALUES (?,?,?,?)", (body["operation_id"], body["session_id"], body["grant_id"], fingerprint))
            conn.execute("DELETE FROM local_dev_grants WHERE session_id=?", (body["session_id"],))
            conn.execute("INSERT INTO local_dev_grants VALUES (?,?,?,?,?)", (body["session_id"], body["grant_id"], body["operation_id"], token_hash, body["expires_at"]))
        except sqlite3.IntegrityError:
            raise GrantRejected("Grant ID conflict.") from None
    return {"grant_id": body["grant_id"]}


def grant_identity(db_path, session_id, token, *, now=None):
    now = now or datetime.now(UTC)
    if not _id(session_id) or not isinstance(token, str) or not _TOKEN.fullmatch(token):
        return False
    try:
        raw = base64.b64decode(token + "=", altchars=b"-_", validate=True)
        if len(raw) != 32 or base64.urlsafe_b64encode(raw).decode().rstrip("=") != token:
            return False
    except (ValueError, binascii.Error):
        return False
    with sqlite3.connect(db_path) as conn:
        row = conn.execute("SELECT g.token_sha256,g.expires_at,s.expires_at,s.state,g.grant_id FROM local_dev_grants g JOIN local_dev_sessions s ON s.session_id=g.session_id WHERE g.session_id=?", (session_id,)).fetchone()
    if row and row[3] == "open" and _timestamp(row[1]) > now and _timestamp(row[2]) > now and hmac.compare_digest(row[0], hashlib.sha256(raw).hexdigest()):
        return row[4]
    return None


def authorize(db_path, session_id, token, *, now=None):
    return bool(grant_identity(db_path, session_id, token, now=now))
