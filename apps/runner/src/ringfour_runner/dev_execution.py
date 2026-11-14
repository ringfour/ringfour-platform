"""Private Runner restart for a committed development generation."""
import asyncio
import hashlib
import json
import platform
import re
import sys
from datetime import UTC, datetime
from pathlib import Path

from fastapi import APIRouter, Request
from packaging.specifiers import SpecifierSet
from packaging.version import Version

from .dev_source import valid_path
from .executions import (
    MAX_EXECUTIONS,
    TERMINAL,
    authorize,
    connection,
    error,
    get_row,
    now,
    record_event,
    run_execution,
    snapshot,
    stop_execution,
    valid_id,
)

router = APIRouter()
_DIRECTORY = re.compile(r"generation-[1-9][0-9]*-[0-9a-f-]{36}\Z")


def initialize(settings):
    with connection(settings) as conn:
        conn.execute("""CREATE TABLE IF NOT EXISTS dev_execution_sessions (
            execution_id TEXT PRIMARY KEY REFERENCES executions(execution_id),
            session_id TEXT NOT NULL REFERENCES dev_sessions(session_id), generation INTEGER NOT NULL)""")


def read_generation(settings, session_id, row):
    """Recheck persisted source against its manifest before any execution."""
    directory = row["directory"]
    if not isinstance(directory, str) or not _DIRECTORY.fullmatch(directory):
        raise ValueError("Invalid generation directory")
    parent = Path(settings.data_dir) / "dev-sessions"
    workspace = parent / session_id
    root = workspace / directory
    if parent.is_symlink() or workspace.is_symlink() or root.is_symlink() or not root.is_dir():
        raise ValueError("Missing generation")
    manifest = json.loads(row["manifest"])
    files = []
    expected = {item["path"]: item for item in manifest["files"]}
    for name, item in sorted(expected.items()):
        if not valid_path(name):
            raise ValueError("Invalid generation path")
        current = root
        for part in name.split("/"):
            current = current / part
            if current.is_symlink():
                raise ValueError("Invalid generation path")
        with current.open("rb") as handle:
            data = handle.read(262145)
        if len(data) != item["size"] or hashlib.sha256(data).hexdigest() != item["sha256"]:
            raise ValueError("Generation changed")
        files.append((name, data))
    actual = set()
    for path in root.rglob("*"):
        if path.is_symlink() or (not path.is_file() and not path.is_dir()):
            raise ValueError("Invalid generation entry")
        if path.is_file():
            actual.add(path.relative_to(root).as_posix())
    if actual != set(expected):
        raise ValueError("Generation changed")
    canonical = [[name, hashlib.sha256(data).hexdigest()] for name, data in files]
    digest = hashlib.sha256(json.dumps(canonical, ensure_ascii=False, separators=(",", ":")).encode()).hexdigest()
    return manifest, files, digest


def environment_key(manifest):
    identity = [manifest["lock_sha256"], manifest["python_requirement"], sys.version, sys.platform, platform.machine()]
    return hashlib.sha256(json.dumps(identity, separators=(",", ":")).encode()).hexdigest()


async def stop_session_execution(app, session_id):
    with connection(app.state.settings) as conn:
        active = conn.execute("""SELECT e.execution_id FROM executions e JOIN dev_execution_sessions d
            ON d.execution_id=e.execution_id WHERE d.session_id=? AND e.state IN ('queued','preparing','running')""", (session_id,)).fetchone()
    if not active:
        return True
    execution_id = active[0]
    await stop_execution(app, execution_id)
    for _ in range(100):
        row = get_row(app.state.settings, execution_id)
        if row and row["state"] in TERMINAL:
            return True
        await asyncio.sleep(0.1)
    return False


@router.post("/v1/dev-sessions/restart")
async def restart(request: Request):
    if not authorize(request):
        return error(401, "unauthenticated", "Runner authentication required.")
    try:
        raw = bytearray()
        async for chunk in request.stream():
            raw.extend(chunk)
            if len(raw) > 4096:
                return error(413, "size_limit", "Restart request exceeds its limit.")
        body = json.loads(raw)
        required = {"operation_id", "session_id", "robot_id", "generation", "execution_id"}
        if (not isinstance(body, dict) or set(body) != required or
                not all(valid_id(body[key]) for key in ("operation_id", "session_id", "robot_id", "execution_id")) or
                type(body["generation"]) is not int or body["generation"] < 1):
            raise ValueError("Invalid restart")
    except (ValueError, UnicodeError):
        return error(400, "invalid_input", "Invalid restart request.")
    app, settings = request.app, request.app.state.settings
    operation_id, session_id, execution_id = body["operation_id"], body["session_id"], body["execution_id"]
    canonical = json.dumps(body, sort_keys=True, separators=(",", ":"))
    async with app.state.dev_session_lock:
        with connection(settings) as conn:
            conn.execute("BEGIN IMMEDIATE")
            op = conn.execute("SELECT request,state,result FROM dev_session_operations WHERE operation_id=?", (operation_id,)).fetchone()
            if op and op["request"] != canonical:
                return error(409, "conflict", "Operation ID was reused with different input.")
            if op and op["state"] == "succeeded":
                row = get_row(settings, execution_id)
                return {"session_id": session_id, "generation": body["generation"], "execution": snapshot(row)} if row else error(409, "conflict", "Execution state is missing.")
            session = conn.execute("SELECT state,expires_at FROM dev_sessions WHERE session_id=?", (session_id,)).fetchone()
            if not session or session["state"] != "ready" or datetime.fromisoformat(session["expires_at"]) <= datetime.now(UTC):
                return error(409, "session_unavailable", "Session is not open.")
            generation = conn.execute("SELECT generation,manifest,directory FROM dev_source_generations WHERE session_id=? ORDER BY generation DESC LIMIT 1", (session_id,)).fetchone()
            if not generation or generation["generation"] != body["generation"]:
                return error(409, "stale_generation", "The development generation is not current.")
            active = conn.execute("""SELECT d.session_id FROM executions e LEFT JOIN dev_execution_sessions d
                ON d.execution_id=e.execution_id WHERE e.state IN ('queued','preparing','running') LIMIT 1""").fetchone()
            if active and active["session_id"] != session_id:
                return error(409, "execution_conflict", "Another execution is active.")
            existing = conn.execute("SELECT execution_id FROM dev_execution_sessions WHERE execution_id=?", (execution_id,)).fetchone()
            if existing and not op:
                return error(409, "conflict", "Execution ID was already used.")
            if not op:
                conn.execute("INSERT INTO dev_session_operations VALUES (?,?,?,'running',NULL)", (operation_id, session_id, canonical))
            conn.commit()
        row = get_row(settings, execution_id)
        if row:
            with connection(settings) as conn:
                mapping = conn.execute("SELECT session_id,generation FROM dev_execution_sessions WHERE execution_id=?", (execution_id,)).fetchone()
                if not mapping or mapping["session_id"] != session_id or mapping["generation"] != body["generation"] or row["robot_id"] != body["robot_id"]:
                    return error(409, "conflict", "Execution ID was already used.")
                conn.execute("UPDATE dev_session_operations SET state='succeeded',result='{}' WHERE operation_id=?", (operation_id,))
            return {"session_id": session_id, "generation": body["generation"], "execution": snapshot(row)}
        try:
            manifest, files, digest = read_generation(settings, session_id, generation)
            requirement, lock = manifest["python_requirement"], manifest["lock_sha256"]
            if Version(".".join(map(str, sys.version_info[:3]))) not in SpecifierSet(requirement):
                raise ValueError("Python version does not satisfy the development requirement")
            workspace = Path(settings.data_dir) / "dev-sessions" / session_id
            environments = workspace / "envs"
            if workspace.is_symlink() or environments.is_symlink():
                raise ValueError("Unsafe environment path")
            environments.mkdir(mode=0o700, exist_ok=True)
            env = environments / environment_key(manifest)
        except (OSError, ValueError, KeyError, TypeError):
            return error(400, "invalid_source", "The committed generation cannot be verified.")
        if not await stop_session_execution(app, session_id):
            return error(503, "stop_pending", "The previous session execution has not stopped.")
        request_body = {"execution_id": execution_id, "robot_id": body["robot_id"], "revision_digest": digest,
                        "code_ref": f"dev:{session_id}:{body['generation']}", "entrypoint": manifest["entrypoint"],
                        "python_requirement": requirement, "lock_sha256": lock}
        request_fingerprint = json.dumps(request_body, sort_keys=True, separators=(",", ":"))
        with connection(settings) as conn:
            conn.execute("BEGIN IMMEDIATE")
            if conn.execute("SELECT 1 FROM executions WHERE state IN ('queued','preparing','running') LIMIT 1").fetchone():
                return error(409, "execution_conflict", "Another execution is active.")
            if conn.execute("SELECT count(*) FROM executions").fetchone()[0] >= MAX_EXECUTIONS:
                return error(503, "capacity_exceeded", "Execution retention capacity reached.")
            if conn.execute("SELECT 1 FROM executions WHERE execution_id=?", (execution_id,)).fetchone():
                return error(409, "conflict", "Execution ID was already used.")
            stamp = now()
            conn.execute("INSERT INTO executions(execution_id,robot_id,request,revision_digest,state,created_at,updated_at) VALUES (?,?,?,?,'queued',?,?)",
                         (execution_id, body["robot_id"], request_fingerprint, digest, stamp, stamp))
            conn.execute("INSERT INTO dev_execution_sessions VALUES (?,?,?)", (execution_id, session_id, body["generation"]))
            record_event(conn, execution_id, "queued", stamp)
            conn.execute("UPDATE dev_session_operations SET state='succeeded',result='{}' WHERE operation_id=?", (operation_id,))
        task = asyncio.create_task(run_execution(app, request_body, files, dev_environment=env))
        app.state.tasks.add(task)
        task.add_done_callback(app.state.tasks.discard)
        return {"session_id": session_id, "generation": body["generation"], "execution": snapshot(get_row(settings, execution_id))}
