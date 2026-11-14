"""Verify and atomically publish a Runner-owned development generation."""
import hashlib
import io
import json
import os
import re
import shutil
import sqlite3
import stat
import zipfile
from datetime import UTC, datetime
from pathlib import Path

from fastapi import APIRouter, Request
from packaging.specifiers import InvalidSpecifier, SpecifierSet

from .executions import authorize, connection, error, safe_relative, valid_id

router = APIRouter()
MAX_ARCHIVE = 1048576
MAX_FILE = 262144
MAX_FILES = 1024
MAX_GENERATIONS = 20
_DIGEST = re.compile(r"[0-9a-f]{64}\Z")
_COMPONENT = re.compile(r"[A-Za-z0-9_.-]+\Z")


def initialize(settings):
    with connection(settings) as conn:
        conn.execute("""CREATE TABLE IF NOT EXISTS dev_source_generations (
            session_id TEXT NOT NULL REFERENCES dev_sessions(session_id), generation INTEGER NOT NULL,
            operation_id TEXT NOT NULL UNIQUE, archive_sha256 TEXT NOT NULL, manifest TEXT NOT NULL,
            directory TEXT NOT NULL, PRIMARY KEY(session_id, generation))""")


def valid_path(name):
    return isinstance(name, str) and 0 < len(name) <= 512 and all(
        part not in ("", ".", "..", "__pycache__") and not part.startswith(".") and _COMPONENT.fullmatch(part)
        for part in name.split("/")
    )


def digest(data):
    return hashlib.sha256(data).hexdigest()


def validate_snapshot(archive_bytes, manifest):
    if not isinstance(manifest, dict) or set(manifest) != {"entrypoint", "python_requirement", "lock_sha256", "files", "deleted_paths"}:
        raise ValueError("Invalid manifest")
    entrypoint, requirement, lock, listing, deletions = (manifest[key] for key in ("entrypoint", "python_requirement", "lock_sha256", "files", "deleted_paths"))
    if (not valid_path(entrypoint) or not entrypoint.endswith(".py") or not isinstance(requirement, str)
            or not 1 <= len(requirement) <= 128 or not isinstance(lock, str) or not _DIGEST.fullmatch(lock)
            or not isinstance(listing, list) or len(listing) > MAX_FILES or not isinstance(deletions, list)
            or len(deletions) > MAX_FILES or any(not valid_path(name) for name in deletions) or len(set(deletions)) != len(deletions)):
        raise ValueError("Invalid manifest")
    try:
        SpecifierSet(requirement)
    except InvalidSpecifier:
        raise ValueError("Invalid Python requirement") from None
    declared = {}
    for item in listing:
        if (not isinstance(item, dict) or set(item) != {"path", "size", "sha256"} or not valid_path(item["path"])
                or type(item["size"]) is not int or not 0 <= item["size"] <= MAX_FILE
                or not isinstance(item["sha256"], str) or not _DIGEST.fullmatch(item["sha256"])
                or item["path"] in declared):
            raise ValueError("Invalid file manifest")
        declared[item["path"]] = item
    if set(deletions) & declared.keys():
        raise ValueError("Deleted file still present")
    files, directories, explicit = {}, set(), set()
    total = 0
    try:
        with zipfile.ZipFile(io.BytesIO(archive_bytes)) as archive:
            if len(archive.infolist()) > MAX_FILES * 2:
                raise ValueError("Too many entries")
            for entry in archive.infolist():
                directory = entry.is_dir()
                name = entry.filename[:-1] if directory else entry.filename
                mode = (entry.external_attr >> 16) & 0o170000
                if not valid_path(name) or name in explicit or mode not in (0, stat.S_IFDIR if directory else stat.S_IFREG):
                    raise ValueError("Invalid ZIP entry")
                explicit.add(name)
                components = name.split("/")
                for index in range(1, len(components)):
                    parent = "/".join(components[:index])
                    if parent in files:
                        raise ValueError("File and directory collide")
                    directories.add(parent)
                if directory:
                    if name in files:
                        raise ValueError("File and directory collide")
                    directories.add(name)
                    continue
                if name in directories or len(files) >= MAX_FILES or entry.file_size > MAX_FILE:
                    raise ValueError("Invalid bundle size or path")
                with archive.open(entry) as source:
                    data = source.read(MAX_FILE + 1)
                total += len(data)
                if len(data) != entry.file_size or len(data) > MAX_FILE or total > MAX_ARCHIVE:
                    raise ValueError("Bundle size limit")
                files[name] = data
    except (OSError, zipfile.BadZipFile, RuntimeError, EOFError) as exc:
        raise ValueError("Invalid archive") from exc
    if set(files) != set(declared) or entrypoint not in files or "requirements.lock" not in files:
        raise ValueError("Archive does not match manifest")
    for name, data in files.items():
        if len(data) != declared[name]["size"] or digest(data) != declared[name]["sha256"]:
            raise ValueError("File digest mismatch")
    if digest(files["requirements.lock"]) != lock:
        raise ValueError("Lock digest mismatch")
    try:
        files["requirements.lock"].decode("utf-8")
    except UnicodeError:
        raise ValueError("Invalid lock encoding") from None
    return files


def read_staged(settings, reference):
    path = safe_relative(reference)
    current = Path(settings.stage_dir)
    if current.is_symlink():
        raise ValueError("Unsafe stage")
    for part in path.parts:
        current = current / part
        if current.is_symlink():
            raise ValueError("Unsafe stage")
    if not current.is_file():
        raise ValueError("Missing stage")
    with current.open("rb") as handle:
        return handle.read(MAX_ARCHIVE + 1)


@router.post("/v1/dev-sessions/sync")
async def sync(request: Request):
    if not authorize(request):
        return error(401, "unauthenticated", "Runner authentication required.")
    try:
        raw = bytearray()
        async for chunk in request.stream():
            raw.extend(chunk)
            if len(raw) > 2097152:
                return error(413, "size_limit", "Sync request exceeds its limit.")
        body = json.loads(raw)
        required = {"operation_id", "session_id", "base_generation", "generation", "archive_sha256", "archive_size", "staged_archive", "manifest"}
        if (not isinstance(body, dict) or set(body) != required or not valid_id(body["operation_id"]) or not valid_id(body["session_id"])
                or type(body["base_generation"]) is not int or body["base_generation"] < 0
                or type(body["generation"]) is not int or body["generation"] != body["base_generation"] + 1
                or type(body["archive_size"]) is not int or not 1 <= body["archive_size"] <= MAX_ARCHIVE
                or not isinstance(body["archive_sha256"], str) or not _DIGEST.fullmatch(body["archive_sha256"])):
            raise ValueError("Invalid sync request")
        archive = read_staged(request.app.state.settings, body["staged_archive"])
        if len(archive) != body["archive_size"] or digest(archive) != body["archive_sha256"]:
            raise ValueError("Archive mismatch")
        files = validate_snapshot(archive, body["manifest"])
    except (ValueError, OSError, TypeError, UnicodeError):
        return error(400, "invalid_source", "Invalid source snapshot or manifest.")
    settings = request.app.state.settings
    session_id, operation_id = body["session_id"], body["operation_id"]
    canonical = json.dumps(body, sort_keys=True, separators=(",", ":"))
    async with request.app.state.dev_session_lock:
        with connection(settings) as conn:
            conn.execute("BEGIN IMMEDIATE")
            operation = conn.execute("SELECT request,state,result FROM dev_session_operations WHERE operation_id=?", (operation_id,)).fetchone()
            if operation:
                if operation["request"] != canonical:
                    return error(409, "conflict", "Operation ID was reused with different input.")
                if operation["state"] == "succeeded":
                    return json.loads(operation["result"])
            session = conn.execute("SELECT state,expires_at FROM dev_sessions WHERE session_id=?", (session_id,)).fetchone()
            if not session or session["state"] != "ready" or datetime.fromisoformat(session["expires_at"]) <= datetime.now(UTC):
                return error(409, "session_unavailable", "Session is not open.")
            prior = conn.execute("SELECT generation,manifest FROM dev_source_generations WHERE session_id=? ORDER BY generation DESC LIMIT 1", (session_id,)).fetchone()
            generation = prior["generation"] if prior else 0
            if body["base_generation"] != generation:
                return error(409, "stale_generation", "The base generation is stale.")
            if generation >= MAX_GENERATIONS:
                return error(409, "capacity_exceeded", "The session generation limit has been reached.")
            old_paths = {item["path"] for item in json.loads(prior["manifest"])["files"]} if prior else set()
            if set(body["manifest"]["deleted_paths"]) != old_paths - files.keys():
                return error(400, "invalid_source", "The deletion list does not match the previous generation.")
            if not operation:
                conn.execute("INSERT INTO dev_session_operations VALUES (?,?,?,'running',NULL)", (operation_id, session_id, canonical))
            conn.commit()
        workspace = Path(settings.data_dir) / "dev-sessions" / session_id
        directory_name = f"generation-{body['generation']}-{operation_id}"
        destination = workspace / directory_name
        temporary = workspace / f".sync-{operation_id}"
        try:
            if workspace.is_symlink() or not workspace.is_dir() or destination.is_symlink() or temporary.is_symlink():
                raise OSError("Unsafe workspace")
            for candidate in (temporary, destination):
                if candidate.exists():
                    shutil.rmtree(candidate)
            temporary.mkdir(mode=0o700)
            for name, data in files.items():
                target = temporary.joinpath(*name.split("/"))
                target.parent.mkdir(parents=True, exist_ok=True)
                with target.open("xb") as handle:
                    handle.write(data)
                    handle.flush()
                    os.fsync(handle.fileno())
            for directory in sorted((path for path in temporary.rglob("*") if path.is_dir()), key=lambda path: len(path.parts), reverse=True):
                fd = os.open(directory, os.O_RDONLY)
                try:
                    os.fsync(fd)
                finally:
                    os.close(fd)
            fd = os.open(temporary, os.O_RDONLY)
            try:
                os.fsync(fd)
            finally:
                os.close(fd)
            os.replace(temporary, destination)
            fd = os.open(workspace, os.O_RDONLY)
            try:
                os.fsync(fd)
            finally:
                os.close(fd)
            result = {"session_id": session_id, "generation": body["generation"], "archive_sha256": body["archive_sha256"]}
            with connection(settings) as conn:
                conn.execute("INSERT INTO dev_source_generations VALUES (?,?,?,?,?,?)", (session_id, body["generation"], operation_id,
                    body["archive_sha256"], json.dumps(body["manifest"], sort_keys=True, separators=(",", ":")), directory_name))
                conn.execute("UPDATE dev_session_operations SET state='succeeded',result=? WHERE operation_id=?", (json.dumps(result, separators=(",", ":")), operation_id))
            return result
        except (OSError, sqlite3.Error):
            return error(503, "storage_unavailable", "Session storage is unavailable.")
