"""Private, authenticated execution service and durable local state."""
import asyncio
import base64
import hashlib
import hmac
import json
import logging
import os
import signal
import sqlite3
import sys
import uuid
from datetime import UTC, datetime
from pathlib import Path, PurePosixPath

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse

router = APIRouter()
logger = logging.getLogger(__name__)
TERMINAL = {"succeeded", "failed", "cancelled"}
OUTPUT_LIMIT = 65536
MAX_EXECUTIONS = 100


def now():
    return datetime.now(UTC).isoformat().replace("+00:00", "Z")


def error(status, code, message):
    return JSONResponse({"error": {"code": code, "message": message, "request_id": str(uuid.uuid4())}}, status_code=status)


def authorize(request):
    secret = request.app.state.settings.auth_token
    presented = request.headers.get("authorization", "")
    return bool(secret) and hmac.compare_digest(presented, f"Bearer {secret}")


def connection(settings):
    conn = sqlite3.connect(str(Path(settings.data_dir) / "executions.db"), timeout=5)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA foreign_keys = ON")
    return conn


def transition(conn, execution_id, state, *, exit_code=None, failure=None):
    stamp = now()
    conn.execute("UPDATE executions SET state=?,updated_at=?,exit_code=?,error=? WHERE execution_id=?",
                 (state, stamp, exit_code, json.dumps(failure) if failure else None, execution_id))
    record_event(conn, execution_id, state, stamp)


def record_event(conn, execution_id, state, stamp):
    robot_id = conn.execute("SELECT robot_id FROM executions WHERE execution_id=?", (execution_id,)).fetchone()[0]
    conn.execute("INSERT INTO events (event_id,robot_id,execution_id,operation_id,occurred_at,event_type,actor,schema_version,payload) VALUES (?,?,?,?,?,?,?,?,?)",
                 (str(uuid.uuid4()), robot_id, execution_id, execution_id, stamp, "execution." + state, "runner", 1, json.dumps({"state": state}, separators=(",", ":"))))


def initialize(settings):
    Path(settings.data_dir).mkdir(parents=True, exist_ok=True)
    with connection(settings) as conn:
        conn.executescript("""
        CREATE TABLE IF NOT EXISTS schema_migrations (
          version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS executions (
          execution_id TEXT PRIMARY KEY, robot_id TEXT NOT NULL, request TEXT NOT NULL, revision_digest TEXT NOT NULL,
          state TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
          exit_code INTEGER, error TEXT, stop_requested INTEGER NOT NULL DEFAULT 0,
          stdout_truncated INTEGER NOT NULL DEFAULT 0, stderr_truncated INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS events (
          sequence INTEGER PRIMARY KEY AUTOINCREMENT, event_id TEXT NOT NULL UNIQUE,
          robot_id TEXT NOT NULL, execution_id TEXT NOT NULL REFERENCES executions(execution_id),
          operation_id TEXT NOT NULL, occurred_at TEXT NOT NULL, received_at TEXT,
          event_type TEXT NOT NULL, actor TEXT NOT NULL, schema_version INTEGER NOT NULL, payload TEXT NOT NULL
        );
        """)
        conn.execute("INSERT OR IGNORE INTO schema_migrations(version,applied_at) VALUES (1,?)", (now(),))
        rows = conn.execute("SELECT execution_id FROM executions WHERE state IN ('queued','preparing','running')").fetchall()
        for row in rows:
            transition(conn, row[0], "failed", failure={"code": "interrupted_unknown_outcome", "message": "Runner restarted while execution outcome was unknown."})


def snapshot(row):
    return {k: (json.loads(row[k]) if k == "error" and row[k] else bool(row[k]) if k == "stop_requested" else row[k])
            for k in ("execution_id", "revision_digest", "state", "created_at", "updated_at", "exit_code", "error", "stop_requested")}


def get_row(settings, execution_id):
    with connection(settings) as conn:
        return conn.execute("SELECT * FROM executions WHERE execution_id=?", (execution_id,)).fetchone()


def safe_relative(value):
    if not isinstance(value, str) or not value or len(value) > 240 or "\\" in value:
        raise ValueError("Invalid relative path")
    path = PurePosixPath(value)
    if path.is_absolute() or any(part in (".", "..") for part in value.split("/")):
        raise ValueError("Invalid relative path")
    return path


def bundle_files(settings, code_ref, entrypoint):
    ref = safe_relative(code_ref)
    entry = safe_relative(entrypoint)
    if entry.suffix != ".py":
        raise ValueError("Entrypoint must be a Python file")
    root = Path(settings.stage_dir)
    current = root
    for part in ref.parts:
        current = current / part
        if current.is_symlink():
            raise ValueError("Symlinks are not permitted")
    if not current.is_dir():
        raise ValueError("Bundle does not exist")
    files = []
    total = 0
    for path in sorted(current.rglob("*")):
        if path.is_symlink():
            raise ValueError("Symlinks are not permitted")
        if path.is_dir():
            continue
        if not path.is_file():
            raise ValueError("Only regular files are permitted")
        if len(files) >= 1024 or path.stat().st_size > 262144:
            raise ValueError("Bundle exceeds size limit")
        with path.open("rb") as handle:
            data = handle.read(262145)
        total += len(data)
        if len(data) > 262144 or total > 1048576:
            raise ValueError("Bundle exceeds size limit")
        files.append((path.relative_to(current).as_posix(), data))
    if not any(name == str(entry) for name, _ in files):
        raise ValueError("Entrypoint not found")
    manifest = [[name, hashlib.sha256(data).hexdigest()] for name, data in files]
    digest = hashlib.sha256(json.dumps(manifest, ensure_ascii=False, separators=(",", ":")).encode()).hexdigest()
    return files, digest


def valid_id(value):
    try:
        return isinstance(value, str) and str(uuid.UUID(value)) == value
    except (ValueError, AttributeError):
        return False


def valid_start(body):
    if not isinstance(body, dict) or set(body) != {"execution_id", "robot_id", "revision_digest", "code_ref", "entrypoint"}:
        return False
    digest = body["revision_digest"]
    return valid_id(body["execution_id"]) and valid_id(body["robot_id"]) and isinstance(digest, str) and len(digest) == 64 and all(c in "0123456789abcdef" for c in digest)


@router.post("/v1/executions")
async def start(request: Request):
    if not authorize(request):
        return error(401, "unauthenticated", "Runner authentication required.")
    try:
        raw = bytearray()
        async for chunk in request.stream():
            raw.extend(chunk)
            if len(raw) > 4096:
                return error(413, "size_limit", "Execution request exceeds 4096 bytes.")
        body = json.loads(raw)
        if not valid_start(body):
            raise ValueError("Invalid execution request")
        fingerprint = json.dumps(body, sort_keys=True, separators=(",", ":"))
        existing = get_row(request.app.state.settings, body["execution_id"])
        if existing:
            if existing["request"] != fingerprint:
                return error(409, "conflict", "Execution ID was used with different input.")
            return JSONResponse(snapshot(existing))
        files, digest = bundle_files(request.app.state.settings, body["code_ref"], body["entrypoint"])
        if digest != body["revision_digest"]:
            raise ValueError("Bundle digest mismatch")
    except (ValueError, OSError, UnicodeError):
        return error(400, "invalid_input", "Invalid execution request or staged bundle.")
    settings = request.app.state.settings
    execution_id = body["execution_id"]
    with connection(settings) as conn:
        conn.execute("BEGIN IMMEDIATE")
        existing = conn.execute("SELECT * FROM executions WHERE execution_id=?", (execution_id,)).fetchone()
        if existing:
            if existing["request"] != fingerprint:
                return error(409, "conflict", "Execution ID was used with different input.")
            return JSONResponse(snapshot(existing))
        if conn.execute("SELECT 1 FROM executions WHERE state IN ('queued','preparing','running') LIMIT 1").fetchone():
            return error(409, "execution_conflict", "Another execution is active.")
        if conn.execute("SELECT count(*) FROM executions").fetchone()[0] >= MAX_EXECUTIONS:
            return error(503, "capacity_exceeded", "Execution retention capacity reached.")
        stamp = now()
        conn.execute("INSERT INTO executions(execution_id,robot_id,request,revision_digest,state,created_at,updated_at) VALUES (?,?,?,?,'queued',?,?)",
                     (execution_id, body["robot_id"], fingerprint, digest, stamp, stamp))
        record_event(conn, execution_id, "queued", stamp)
    task = asyncio.create_task(run_execution(request.app, body, files))
    request.app.state.tasks.add(task)
    task.add_done_callback(request.app.state.tasks.discard)
    return JSONResponse(snapshot(get_row(settings, execution_id)), status_code=201)


async def capture(stream, path, settings, execution_id, name):
    total = 0
    with path.open("wb") as target:
        while chunk := await stream.read(8192):
            take = min(len(chunk), OUTPUT_LIMIT - total)
            target.write(chunk[:take])
            target.flush()
            total += take
            if take < len(chunk):
                with connection(settings) as conn:
                    conn.execute(f"UPDATE executions SET {name}_truncated=1 WHERE execution_id=?", (execution_id,))


async def run_execution(app, body, files):
    settings = app.state.settings
    execution_id = body["execution_id"]
    path = Path(settings.data_dir) / "executions" / execution_id
    try:
        path.mkdir(parents=True, exist_ok=True)
        code = path / "code"
        for name, data in files:
            target = code / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
            if hashlib.sha256(target.read_bytes()).digest() != hashlib.sha256(data).digest():
                raise OSError("Staged copy verification failed")
        with connection(settings) as conn:
            row = conn.execute("SELECT * FROM executions WHERE execution_id=?", (execution_id,)).fetchone()
            if row["stop_requested"]:
                transition(conn, execution_id, "cancelled")
                return
            transition(conn, execution_id, "preparing")
        venv = Path(settings.data_dir) / "venvs" / body["revision_digest"]
        try:
            proc = await asyncio.create_subprocess_exec("uv", "venv", "--allow-existing", "--no-python-downloads", "--python", sys.executable, str(venv), stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE, start_new_session=True)
        except OSError:
            with connection(settings) as conn:
                transition(conn, execution_id, "failed", failure={"code": "preparation_failed", "message": "Unable to prepare the Python environment."})
            return
        app.state.processes[execution_id] = proc
        await proc.communicate()
        if proc.returncode:
            row = get_row(settings, execution_id)
            with connection(settings) as conn:
                transition(conn, execution_id, "cancelled" if row["stop_requested"] else "failed", failure=None if row["stop_requested"] else {"code": "preparation_failed", "message": "Unable to prepare the Python environment."})
            return
        if get_row(settings, execution_id)["stop_requested"]:
            with connection(settings) as conn:
                transition(conn, execution_id, "cancelled")
            return
        with connection(settings) as conn:
            transition(conn, execution_id, "running")
        proc = await asyncio.create_subprocess_exec(str(venv / "bin" / "python"), str(code / body["entrypoint"]), cwd=str(code), stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE, start_new_session=True)
        app.state.processes[execution_id] = proc
        if get_row(settings, execution_id)["stop_requested"] and proc.returncode is None:
            try:
                os.killpg(proc.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
            else:
                asyncio.create_task(escalate(proc))
        await asyncio.gather(capture(proc.stdout, path / "stdout", settings, execution_id, "stdout"), capture(proc.stderr, path / "stderr", settings, execution_id, "stderr"), proc.wait())
        row = get_row(settings, execution_id)
        state = "cancelled" if row["stop_requested"] else "succeeded" if proc.returncode == 0 else "failed"
        failure = {"code": "process_failed", "message": "Execution exited with a nonzero code."} if state == "failed" else None
        with connection(settings) as conn:
            transition(conn, execution_id, state, exit_code=proc.returncode, failure=failure)
    except Exception:
        logger.exception("Runner execution %s failed unexpectedly", execution_id)
        row = get_row(settings, execution_id)
        if row and row["state"] not in TERMINAL:
            with connection(settings) as conn:
                transition(conn, execution_id, "failed", failure={"code": "execution_failed", "message": "Runner could not complete the execution."})
    finally:
        app.state.processes.pop(execution_id, None)


async def escalate(proc):
    try:
        await asyncio.wait_for(asyncio.shield(proc.wait()), timeout=3)
    except TimeoutError:
        try:
            os.killpg(proc.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass


@router.get("/v1/executions/{execution_id}")
async def get_execution(request: Request, execution_id: str):
    if not authorize(request):
        return error(401, "unauthenticated", "Runner authentication required.")
    row = get_row(request.app.state.settings, execution_id)
    return JSONResponse(snapshot(row)) if row else error(404, "not_found", "Execution not found.")


@router.post("/v1/executions/{execution_id}/stop")
async def stop(request: Request, execution_id: str):
    if not authorize(request):
        return error(401, "unauthenticated", "Runner authentication required.")
    settings = request.app.state.settings
    row = get_row(settings, execution_id)
    if not row:
        return error(404, "not_found", "Execution not found.")
    if row["state"] not in TERMINAL:
        with connection(settings) as conn:
            stamp = now()
            changed = conn.execute("UPDATE executions SET stop_requested=1,updated_at=? WHERE execution_id=? AND stop_requested=0 AND state NOT IN ('succeeded','failed','cancelled')", (stamp, execution_id)).rowcount
            if changed:
                record_event(conn, execution_id, "stop_requested", stamp)
        proc = request.app.state.processes.get(execution_id)
        if proc and proc.returncode is None:
            try:
                os.killpg(proc.pid, signal.SIGTERM)
                await asyncio.wait_for(asyncio.shield(proc.wait()), timeout=3)
            except TimeoutError:
                os.killpg(proc.pid, signal.SIGKILL)
                await proc.wait()
            except ProcessLookupError:
                pass
    return JSONResponse(snapshot(get_row(settings, execution_id)))


@router.get("/v1/executions/{execution_id}/output")
async def output(request: Request, execution_id: str, stream: str = "", offset: str = "", limit: str = ""):
    if not authorize(request):
        return error(401, "unauthenticated", "Runner authentication required.")
    try:
        start_offset, chunk_limit = int(offset), int(limit)
    except ValueError:
        return error(400, "invalid_input", "Invalid output range or stream.")
    if stream not in {"stdout", "stderr"} or not 0 <= start_offset <= OUTPUT_LIMIT or not 1 <= chunk_limit <= 16384:
        return error(400, "invalid_input", "Invalid output range or stream.")
    settings = request.app.state.settings
    row = get_row(settings, execution_id)
    if not row:
        return error(404, "not_found", "Execution not found.")
    path = Path(settings.data_dir) / "executions" / execution_id / stream
    data = b""
    if path.exists():
        size = path.stat().st_size
        if start_offset < size:
            with path.open("rb") as handle:
                handle.seek(start_offset)
                data = handle.read(chunk_limit)
    else:
        size = 0
    next_offset = start_offset + len(data)
    return {"execution_id": execution_id, "stream": stream, "offset": start_offset, "next_offset": next_offset,
            "data_base64": base64.b64encode(data).decode(), "eof": row["state"] in TERMINAL and next_offset >= size,
            "truncated": bool(row[f"{stream}_truncated"])}
