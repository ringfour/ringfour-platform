"""Private, authenticated execution service and durable local state."""
import asyncio
import base64
import codecs
import hashlib
import hmac
import io
import json
import logging
import os
import shutil
import signal
import sqlite3
import sys
import uuid
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path, PurePosixPath

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse, StreamingResponse
from packaging.specifiers import InvalidSpecifier, SpecifierSet
from packaging.version import Version
from starlette.background import BackgroundTask

from . import artifacts
from .preparation import prepared_environment

router = APIRouter()
logger = logging.getLogger(__name__)
TERMINAL = {"succeeded", "failed", "cancelled"}
OUTPUT_LIMIT = 65536


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


def has_storage(settings, required_bytes=0):
    """Preserve a free-space reserve without deleting unsynchronized execution data."""
    try:
        return shutil.disk_usage(settings.data_dir).free >= settings.min_free_bytes + required_bytes
    except OSError:
        return False


def transition(conn, execution_id, state, *, exit_code=None, failure=None):
    stamp = now()
    if state in TERMINAL:
        conn.execute("UPDATE executions SET artifact_state='failed',artifact_error=? WHERE execution_id=? AND artifact_state='pending'",
                     (json.dumps({"code": "artifact_collection_incomplete", "message": "Artifact collection did not complete."}), execution_id))
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
        CREATE TABLE IF NOT EXISTS live_artifacts (
          artifact_id TEXT PRIMARY KEY, execution_id TEXT NOT NULL REFERENCES executions(execution_id),
          path TEXT NOT NULL, size INTEGER NOT NULL, sha256 TEXT NOT NULL, content_type TEXT NOT NULL,
          state TEXT NOT NULL, created_at TEXT NOT NULL,
          UNIQUE(execution_id, path),
          CHECK(state IN ('pending','delivered'))
        );
        """)
        columns = {row[1] for row in conn.execute("PRAGMA table_info(executions)")}
        if "artifact_state" not in columns:
            conn.execute("ALTER TABLE executions ADD COLUMN artifact_state TEXT NOT NULL DEFAULT 'unknown'")
        if "artifact_error" not in columns:
            conn.execute("ALTER TABLE executions ADD COLUMN artifact_error TEXT")
        conn.execute("INSERT OR IGNORE INTO schema_migrations(version,applied_at) VALUES (1,?)", (now(),))
        rows = conn.execute("SELECT execution_id FROM executions WHERE state IN ('queued','preparing','running')").fetchall()
        for row in rows:
            transition(conn, row[0], "failed", failure={"code": "interrupted_unknown_outcome", "message": "Runner restarted while execution outcome was unknown."})
    # A crash during collection can leave an unpublished snapshot. The source
    # artifact-output directory remains available for operator recovery.
    for row in rows:
        shutil.rmtree(Path(settings.data_dir) / "executions" / row[0] / "artifact-snapshot.pending", ignore_errors=True)


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


@dataclass(frozen=True)
class StagedFile:
    root: Path
    name: str
    size: int
    sha256: str


def entry_size(data):
    return data.size if isinstance(data, StagedFile) else len(data)


def copy_entry(name, data, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    if not isinstance(data, StagedFile):
        target.write_bytes(data)
        if hashlib.sha256(target.read_bytes()).digest() != hashlib.sha256(data).digest():
            raise OSError("Staged copy verification failed")
        return
    checksum = hashlib.sha256()
    length = 0
    with artifacts.open_regular_under(data.root, name) as source, target.open("xb") as output:
        while chunk := source.read(65536):
            length += len(chunk)
            if length > data.size:
                raise OSError("Staged file changed")
            checksum.update(chunk)
            output.write(chunk)
    if length != data.size or checksum.hexdigest() != data.sha256:
        raise OSError("Staged file changed")


def verify_lock(data, expected):
    source = artifacts.open_regular_under(data.root, data.name) if isinstance(data, StagedFile) else io.BytesIO(data)
    checksum = hashlib.sha256()
    decoder = codecs.getincrementaldecoder("utf-8")("strict")
    length = 0
    with source:
        while chunk := source.read(65536):
            length += len(chunk)
            checksum.update(chunk)
            decoder.decode(chunk)
        decoder.decode(b"", final=True)
    if checksum.hexdigest() != expected or (isinstance(data, StagedFile) and (length != data.size or checksum.hexdigest() != data.sha256)):
        raise ValueError("Dependency lock mismatch")


def bundle_files(settings, code_ref, entrypoint):
    ref = safe_relative(code_ref)
    entry = safe_relative(entrypoint)
    if entry.suffix != ".py":
        raise ValueError("Entrypoint must be a Python file")
    current = Path(settings.stage_dir)
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
        name = path.relative_to(current).as_posix()
        if len(files) >= 1024:
            raise ValueError("Bundle exceeds file count limit")
        with artifacts.open_regular_under(current, name) as handle:
            size = os.fstat(handle.fileno()).st_size
            checksum = hashlib.file_digest(handle, "sha256").hexdigest()
        total += size
        if total > 9007199254740991:
            raise ValueError("Bundle exceeds supported exact integer range")
        files.append((name, StagedFile(current, name, size, checksum)))
    if not any(name == str(entry) for name, _ in files):
        raise ValueError("Entrypoint not found")
    manifest = [[name, data.sha256] for name, data in files]
    digest = hashlib.sha256(json.dumps(manifest, ensure_ascii=False, separators=(",", ":")).encode()).hexdigest()
    return files, digest


def valid_id(value):
    try:
        return isinstance(value, str) and str(uuid.UUID(value)) == value
    except (ValueError, AttributeError):
        return False


def valid_start(body):
    required = {"execution_id", "robot_id", "revision_digest", "code_ref", "entrypoint"}
    if not isinstance(body, dict) or set(body) not in (required, required | {"python_requirement", "lock_sha256"}):
        return False
    digest = body["revision_digest"]
    if not (valid_id(body["execution_id"]) and valid_id(body["robot_id"]) and isinstance(digest, str) and len(digest) == 64 and all(c in "0123456789abcdef" for c in digest)):
        return False
    if "python_requirement" in body:
        requirement, lock = body["python_requirement"], body["lock_sha256"]
        if not isinstance(requirement, str) or not 1 <= len(requirement) <= 128 or not isinstance(lock, str) or len(lock) != 64 or any(c not in "0123456789abcdef" for c in lock):
            return False
        try:
            if Version(".".join(map(str, sys.version_info[:3]))) not in SpecifierSet(requirement):
                return False
        except InvalidSpecifier:
            return False
    return True


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
        files, digest = await asyncio.to_thread(bundle_files, request.app.state.settings, body["code_ref"], body["entrypoint"])
        if digest != body["revision_digest"]:
            raise ValueError("Bundle digest mismatch")
        if "lock_sha256" in body:
            lock = next((data for name, data in files if name == "requirements.lock"), None)
            if lock is None:
                raise ValueError("Dependency lock missing")
            await asyncio.to_thread(verify_lock, lock, body["lock_sha256"])
    except (ValueError, OSError, UnicodeError):
        return error(400, "invalid_input", "Invalid execution request or staged bundle.")
    settings = request.app.state.settings
    if request.app.state.prepare_lock.locked():
        return error(409, "execution_conflict", "Runner is preparing a revision.")
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
        if not has_storage(settings, sum(entry_size(data) for _, data in files)):
            return error(503, "insufficient_storage", "Runner storage is below the configured free-space reserve.")
        stamp = now()
        conn.execute("INSERT INTO executions(execution_id,robot_id,request,revision_digest,state,created_at,updated_at,artifact_state) VALUES (?,?,?,?,'queued',?,?,'pending')",
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


async def run_execution(app, body, files, *, dev_environment=None):
    settings = app.state.settings
    execution_id = body["execution_id"]
    path = Path(settings.data_dir) / "executions" / execution_id
    try:
        path.mkdir(parents=True, exist_ok=True)
        code = path / "code"
        for name, data in files:
            target = code / name
            await asyncio.to_thread(copy_entry, name, data, target)
        with connection(settings) as conn:
            row = conn.execute("SELECT * FROM executions WHERE execution_id=?", (execution_id,)).fetchone()
            if row["stop_requested"]:
                transition(conn, execution_id, "cancelled")
                return
            transition(conn, execution_id, "preparing")
        prepared = prepared_environment(settings, body["revision_digest"], body.get("python_requirement"), body.get("lock_sha256")) if dev_environment is None else None
        if dev_environment is not None:
            venv = dev_environment
            marker = venv.with_suffix(".ready")
            if marker.is_file() and not marker.is_symlink() and (venv / "bin" / "python").is_file():
                prepared = venv
            else:
                if venv.is_symlink() or marker.is_symlink():
                    raise OSError("Unsafe development environment")
                if venv.exists():
                    shutil.rmtree(venv)
                marker.unlink(missing_ok=True)
        else:
            venv = prepared or Path(settings.data_dir) / "venvs" / body["revision_digest"]
        if prepared is not None:
            with connection(settings) as conn:
                if conn.execute("SELECT stop_requested FROM executions WHERE execution_id=?", (execution_id,)).fetchone()[0]:
                    transition(conn, execution_id, "cancelled")
                    return
                transition(conn, execution_id, "running")
        else:
            await prepare_execution_environment(app, body, code, venv, execution_id)
            row = get_row(settings, execution_id)
            if row["state"] in TERMINAL:
                return
            if dev_environment is not None:
                temporary = marker.with_suffix(".pending")
                with temporary.open("w") as handle:
                    handle.write("ready\n")
                    handle.flush()
                    os.fsync(handle.fileno())
                os.replace(temporary, marker)

        artifact_dir = path / "artifact-output"
        artifact_dir.mkdir(exist_ok=True)
        environment = {**os.environ, "RINGFOUR_ARTIFACT_DIR": str(artifact_dir)}
        environment["RINGFOUR_UI_PORT"] = str(settings.ui_port)
        environment.pop("RUNNER_AUTH_TOKEN", None)
        environment.pop("RINGFOUR_LOGBOOK_TOKEN", None)
        environment.pop("RINGFOUR_LOGBOOK_URL", None)
        environment.pop("RINGFOUR_ARTIFACT_TOKEN", None)
        environment.pop("RINGFOUR_ARTIFACT_URL", None)
        environment.pop("RINGFOUR_INTERACTION_TOKEN", None)
        environment.pop("RINGFOUR_INTERACTION_URL", None)
        if settings.logbook_url and settings.auth_token:
            mac = hmac.new(settings.auth_token.encode(), ("ringfour-logbook-v1:" + execution_id).encode(), hashlib.sha256).hexdigest()
            environment["RINGFOUR_LOGBOOK_URL"] = settings.logbook_url
            environment["RINGFOUR_LOGBOOK_TOKEN"] = f"{execution_id}:{mac}"
            environment["RINGFOUR_ARTIFACT_URL"] = settings.logbook_url
            environment["RINGFOUR_ARTIFACT_TOKEN"] = f"{execution_id}:{mac}"
            environment["RINGFOUR_INTERACTION_URL"] = settings.logbook_url
            environment["RINGFOUR_INTERACTION_TOKEN"] = f"{execution_id}:{mac}"
        app.state.ui_started_at[execution_id] = asyncio.get_running_loop().time()
        proc = await asyncio.create_subprocess_exec(str(venv / "bin" / "python"), str(code / body["entrypoint"]), cwd=str(code), env=environment, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE, start_new_session=True)
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
        artifact_error = None
        try:
            await asyncio.to_thread(artifacts.collect, artifact_dir, path / "artifact-snapshot", settings.min_free_bytes)
        except artifacts.ArtifactError:
            artifact_error = {"code": "artifact_collection_failed", "message": "Execution artifacts could not be collected."}
        with connection(settings) as conn:
            conn.execute("UPDATE executions SET artifact_state=?,artifact_error=? WHERE execution_id=?",
                         ("failed" if artifact_error else "ready", json.dumps(artifact_error) if artifact_error else None, execution_id))
            transition(conn, execution_id, state, exit_code=proc.returncode, failure=failure)
    except Exception:
        logger.exception("Runner execution %s failed unexpectedly", execution_id)
        row = get_row(settings, execution_id)
        if row and row["state"] not in TERMINAL:
            with connection(settings) as conn:
                transition(conn, execution_id, "failed", failure={"code": "execution_failed", "message": "Runner could not complete the execution."})
    finally:
        app.state.processes.pop(execution_id, None)
        app.state.ui_started_at.pop(execution_id, None)


async def prepare_execution_environment(app, body, code, venv, execution_id):
    settings = app.state.settings
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
    if "lock_sha256" in body:
        proc = await asyncio.create_subprocess_exec("uv", "pip", "sync", "--require-hashes", "--python", str(venv / "bin" / "python"), str(code / "requirements.lock"), stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE, start_new_session=True)
        app.state.processes[execution_id] = proc
        await proc.communicate()
        row = get_row(settings, execution_id)
        if proc.returncode or row["stop_requested"]:
            with connection(settings) as conn:
                transition(conn, execution_id, "cancelled" if row["stop_requested"] else "failed", failure=None if row["stop_requested"] else {"code": "preparation_failed", "message": "Unable to install the locked dependencies."})
            return
    with connection(settings) as conn:
        transition(conn, execution_id, "running")

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


async def stop_execution(app, execution_id):
    settings = app.state.settings
    row = get_row(settings, execution_id)
    if not row:
        return None
    if row["state"] not in TERMINAL:
        with connection(settings) as conn:
            stamp = now()
            changed = conn.execute("UPDATE executions SET stop_requested=1,updated_at=? WHERE execution_id=? AND stop_requested=0 AND state NOT IN ('succeeded','failed','cancelled')", (stamp, execution_id)).rowcount
            if changed:
                record_event(conn, execution_id, "stop_requested", stamp)
        proc = app.state.processes.get(execution_id)
        if proc and proc.returncode is None:
            try:
                os.killpg(proc.pid, signal.SIGTERM)
                await asyncio.wait_for(asyncio.shield(proc.wait()), timeout=3)
            except TimeoutError:
                os.killpg(proc.pid, signal.SIGKILL)
                await proc.wait()
            except ProcessLookupError:
                pass
    return get_row(settings, execution_id)


@router.post("/v1/executions/{execution_id}/stop")
async def stop(request: Request, execution_id: str):
    if not authorize(request):
        return error(401, "unauthenticated", "Runner authentication required.")
    row = await stop_execution(request.app, execution_id)
    return JSONResponse(snapshot(row)) if row else error(404, "not_found", "Execution not found.")


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


@router.get("/v1/executions/{execution_id}/artifacts")
async def artifact_list(request: Request, execution_id: str):
    if not authorize(request):
        return error(401, "unauthenticated", "Runner authentication required.")
    row = get_row(request.app.state.settings, execution_id)
    if not row:
        return error(404, "not_found", "Execution not found.")
    if row["state"] not in TERMINAL:
        return error(409, "execution_not_terminal", "Execution is not terminal.")
    directory = Path(request.app.state.settings.data_dir) / "executions" / execution_id / "artifact-snapshot"
    return {"execution_id": execution_id, "artifacts": artifacts.listing(directory), "artifact_state": row["artifact_state"],
            "artifact_error": json.loads(row["artifact_error"]) if row["artifact_error"] else None}


@router.get("/v1/executions/{execution_id}/artifacts/download")
async def artifact_download(request: Request, execution_id: str, path: str = ""):
    if not authorize(request):
        return error(401, "unauthenticated", "Runner authentication required.")
    if not artifacts.valid_path(path):
        return error(400, "invalid_input", "Invalid artifact path.")
    row = get_row(request.app.state.settings, execution_id)
    if not row:
        return error(404, "not_found", "Execution not found.")
    if row["state"] not in TERMINAL:
        return error(409, "execution_not_terminal", "Execution is not terminal.")
    directory = Path(request.app.state.settings.data_dir) / "executions" / execution_id / "artifact-snapshot"
    entry = next((item for item in artifacts.listing(directory) if item["path"] == path), None)
    if entry is None:
        return error(404, "not_found", "Artifact not found.")
    try:
        source = artifacts.open_regular_under(directory / "files", path)
        if os.fstat(source.fileno()).st_size != entry["size"]:
            source.close()
            raise OSError("Artifact size mismatch")
    except (OSError, artifacts.ArtifactError):
        return error(503, "storage_unavailable", "Artifact snapshot is unavailable.")

    def chunks():
        while chunk := source.read(65536):
            yield chunk

    return StreamingResponse(chunks(), media_type="application/octet-stream",
        headers={"Content-Length": str(entry["size"])}, background=BackgroundTask(source.close))


@router.get("/v1/executions/{execution_id}/artifacts/content")
async def artifact_content(request: Request, execution_id: str, path: str = "", offset: str = "", limit: str = ""):
    if not authorize(request):
        return error(401, "unauthenticated", "Runner authentication required.")
    try:
        start_offset, chunk_limit = int(offset), int(limit)
    except ValueError:
        return error(400, "invalid_input", "Invalid artifact range or path.")
    if not artifacts.valid_path(path) or not 0 <= start_offset <= 9007199254740991 or not 1 <= chunk_limit <= 16384:
        return error(400, "invalid_input", "Invalid artifact range or path.")
    row = get_row(request.app.state.settings, execution_id)
    if not row:
        return error(404, "not_found", "Execution not found.")
    if row["state"] not in TERMINAL:
        return error(409, "execution_not_terminal", "Execution is not terminal.")
    directory = Path(request.app.state.settings.data_dir) / "executions" / execution_id / "artifact-snapshot"
    entry = next((item for item in artifacts.listing(directory) if item["path"] == path), None)
    if entry is None:
        return error(404, "not_found", "Artifact not found.")
    with (directory / "files" / path).open("rb") as source:
        source.seek(start_offset)
        data = source.read(chunk_limit)
    next_offset = start_offset + len(data)
    return {"execution_id": execution_id, "path": path, "offset": start_offset, "next_offset": next_offset,
            "data_base64": base64.b64encode(data).decode(), "eof": next_offset >= entry["size"]}


def live_artifact(settings, execution_id, artifact_id):
    with connection(settings) as conn:
        return conn.execute("SELECT * FROM live_artifacts WHERE execution_id=? AND artifact_id=?", (execution_id, artifact_id)).fetchone()


def live_snapshot(settings, execution_id, artifact_id, path, content_type):
    if not valid_id(artifact_id) or not artifacts.valid_path(path) or content_type not in {"image/jpeg", "image/png", "application/octet-stream"}:
        raise ValueError("Invalid live artifact")
    row = get_row(settings, execution_id)
    if not row or row["state"] != "running":
        raise RuntimeError("Execution is not running")
    existing = live_artifact(settings, execution_id, artifact_id)
    if existing:
        if existing["path"] != path or existing["content_type"] != content_type:
            raise FileExistsError("Artifact ID conflict")
        return dict(existing)
    root = Path(settings.data_dir) / "executions" / execution_id / "artifact-output"
    destination = Path(settings.data_dir) / "executions" / execution_id / "live-artifacts" / artifact_id
    destination.mkdir(parents=True, exist_ok=False)
    temporary = destination / "content.pending"
    checksum = hashlib.sha256()
    size = 0
    try:
        if not has_storage(settings):
            raise artifacts.ArtifactError("Insufficient artifact storage")
        with artifacts.open_regular_under(root, path) as source, temporary.open("xb") as output:
            while chunk := source.read(65536):
                size += len(chunk)
                if size > artifacts.MAX_EXACT_SIZE or not has_storage(settings, len(chunk)):
                    raise artifacts.ArtifactError("Artifact exceeds storage limits")
                checksum.update(chunk)
                output.write(chunk)
            output.flush()
            os.fsync(output.fileno())
        temporary.replace(destination / "content")
        with connection(settings) as conn:
            if conn.execute("SELECT count(*) FROM live_artifacts WHERE execution_id=?", (execution_id,)).fetchone()[0] >= 64:
                raise artifacts.ArtifactError("Live artifact capacity reached")
            conn.execute("INSERT INTO live_artifacts VALUES (?,?,?,?,?,?,?,?)",
                (artifact_id, execution_id, path, size, checksum.hexdigest(), content_type, "pending", now()))
        return dict(live_artifact(settings, execution_id, artifact_id))
    except BaseException:
        shutil.rmtree(destination, ignore_errors=True)
        raise


@router.post("/v1/executions/{execution_id}/live-artifacts")
async def finalize_live_artifact(request: Request, execution_id: str):
    if not authorize(request):
        return error(401, "unauthenticated", "Runner authentication required.")
    try:
        body = await request.json()
        if not isinstance(body, dict) or set(body) != {"artifact_id", "path", "content_type"}:
            raise ValueError
        row = await asyncio.to_thread(live_snapshot, request.app.state.settings, execution_id,
                                      body["artifact_id"], body["path"], body["content_type"])
    except RuntimeError:
        return error(409, "execution_not_running", "Execution is not running.")
    except FileExistsError:
        return error(409, "conflict", "Artifact ID conflicts with an existing snapshot.")
    except (ValueError, TypeError, json.JSONDecodeError):
        return error(400, "invalid_input", "Invalid live artifact request.")
    except (OSError, sqlite3.Error, artifacts.ArtifactError):
        return error(503, "storage_unavailable", "Artifact snapshot could not be finalized.")
    return {"artifact_id": row["artifact_id"], "path": row["path"], "size": row["size"],
            "sha256": row["sha256"], "content_type": row["content_type"], "state": "pending"}


@router.get("/v1/executions/{execution_id}/live-artifacts/{artifact_id}/download")
async def download_live_artifact(request: Request, execution_id: str, artifact_id: str):
    if not authorize(request):
        return error(401, "unauthenticated", "Runner authentication required.")
    row = live_artifact(request.app.state.settings, execution_id, artifact_id)
    if not row:
        return error(404, "not_found", "Artifact not found.")
    path = Path(request.app.state.settings.data_dir) / "executions" / execution_id / "live-artifacts" / artifact_id / "content"
    try:
        source = path.open("rb")
        if os.fstat(source.fileno()).st_size != row["size"]:
            raise OSError
    except OSError:
        return error(503, "storage_unavailable", "Artifact snapshot is unavailable.")
    def chunks():
        while chunk := source.read(65536):
            yield chunk
    return StreamingResponse(chunks(), media_type=row["content_type"], headers={"Content-Length": str(row["size"])},
                             background=BackgroundTask(source.close))


@router.post("/v1/executions/{execution_id}/live-artifacts/{artifact_id}/ack")
async def acknowledge_live_artifact(request: Request, execution_id: str, artifact_id: str):
    if not authorize(request):
        return error(401, "unauthenticated", "Runner authentication required.")
    row = live_artifact(request.app.state.settings, execution_id, artifact_id)
    if not row:
        return error(404, "not_found", "Artifact not found.")
    with connection(request.app.state.settings) as conn:
        conn.execute("UPDATE live_artifacts SET state='delivered' WHERE execution_id=? AND artifact_id=?", (execution_id, artifact_id))
    shutil.rmtree(Path(request.app.state.settings.data_dir) / "executions" / execution_id / "live-artifacts" / artifact_id, ignore_errors=True)
    return JSONResponse(status_code=204, content=None)
