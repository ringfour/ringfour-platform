"""Durable, private development-workspace creation and removal."""
import json
import shutil
from datetime import datetime
from pathlib import Path

from fastapi import APIRouter, Request

from .executions import authorize, connection, error, valid_id

router = APIRouter()


def initialize(settings):
    with connection(settings) as conn:
        conn.executescript("""
        CREATE TABLE IF NOT EXISTS dev_sessions (
          session_id TEXT PRIMARY KEY, expires_at TEXT NOT NULL,
          state TEXT NOT NULL CHECK (state IN ('creating', 'ready', 'closing', 'closed'))
        );
        CREATE TABLE IF NOT EXISTS dev_session_operations (
          operation_id TEXT PRIMARY KEY, session_id TEXT NOT NULL,
          request TEXT NOT NULL, state TEXT NOT NULL CHECK (state IN ('running', 'succeeded')),
          result TEXT
        );
        """)


def validate(body):
    if not isinstance(body, dict) or set(body) != {"operation_id", "session_id", "expires_at", "reason"}:
        return False
    if not valid_id(body["operation_id"]) or not valid_id(body["session_id"]):
        return False
    if body["reason"] not in ("create", "close", "expire") or not isinstance(body["expires_at"], str):
        return False
    try:
        stamp = body["expires_at"]
        return stamp.endswith("Z") and datetime.fromisoformat(stamp).utcoffset().total_seconds() == 0
    except (ValueError, OverflowError):
        return False


@router.post("/v1/dev-sessions/lifecycle")
async def lifecycle(request: Request):
    if not authorize(request):
        return error(401, "unauthenticated", "Runner authentication required.")
    try:
        raw = bytearray()
        async for chunk in request.stream():
            raw.extend(chunk)
            if len(raw) > 4096:
                return error(413, "size_limit", "Lifecycle request exceeds 4096 bytes.")
        body = json.loads(raw)
        if not validate(body):
            raise ValueError("invalid lifecycle request")
    except (ValueError, UnicodeDecodeError):
        return error(400, "invalid_input", "Invalid lifecycle request.")
    settings = request.app.state.settings
    session_id, operation_id, reason = body["session_id"], body["operation_id"], body["reason"]
    canonical = json.dumps(body, sort_keys=True, separators=(",", ":"))
    async with request.app.state.dev_session_lock:
        with connection(settings) as conn:
            conn.execute("BEGIN IMMEDIATE")
            old = conn.execute("SELECT request, state, result FROM dev_session_operations WHERE operation_id=?", (operation_id,)).fetchone()
            if old:
                if old["request"] != canonical:
                    return error(409, "conflict", "Operation ID was reused with different input.")
                if old["state"] == "succeeded":
                    return json.loads(old["result"])
            session = conn.execute("SELECT state, expires_at FROM dev_sessions WHERE session_id=?", (session_id,)).fetchone()
            if session and session["expires_at"] != body["expires_at"]:
                return error(409, "conflict", "Session expiry does not match.")
            if reason == "create" and session and session["state"] in ("closing", "closed"):
                return error(409, "conflict", "Session is closed.")
            if reason == "create" and session and not old:
                return error(409, "conflict", "Session already exists.")
            if not old:
                conn.execute("INSERT INTO dev_session_operations VALUES (?,?,?,'running',NULL)", (operation_id, session_id, canonical))
            if session is None:
                conn.execute("INSERT INTO dev_sessions VALUES (?,?,?)", (session_id, body["expires_at"], "creating" if reason == "create" else "closing"))
            elif reason != "create":
                conn.execute("UPDATE dev_sessions SET state='closing' WHERE session_id=?", (session_id,))
            conn.commit()
        root = Path(settings.data_dir) / "dev-sessions"
        workspace = root / session_id
        try:
            root.mkdir(mode=0o700, exist_ok=True)
            if root.is_symlink() or workspace.is_symlink():
                raise OSError("unsafe workspace path")
            if reason == "create":
                workspace.mkdir(mode=0o700, exist_ok=True)
                final_state = "ready"
            else:
                from .dev_execution import stop_session_execution
                if not await stop_session_execution(request.app, session_id):
                    return error(503, "stop_pending", "The session execution has not stopped.")
                if workspace.exists():
                    shutil.rmtree(workspace)
                final_state = "closed"
            result = {"session_id": session_id, "state": final_state, "generation": 0}
            with connection(settings) as conn:
                conn.execute("UPDATE dev_sessions SET state=? WHERE session_id=?", (final_state, session_id))
                conn.execute("UPDATE dev_session_operations SET state='succeeded', result=? WHERE operation_id=?", (json.dumps(result, separators=(",", ":")), operation_id))
            return result
        except OSError:
            return error(503, "storage_unavailable", "Session storage is unavailable.")
