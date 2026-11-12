"""Stage verified scripts and replicate Runner output through durable offsets."""
import asyncio
import base64
import hashlib
import io
import json
import logging
import os
import re
import shutil
import sqlite3
import stat
import tempfile
import uuid
import zipfile
from pathlib import Path

import httpx

from . import runner
from .enrollment import credential_error, load_identity

logger = logging.getLogger(__name__)
MAX_ARCHIVE = 1048576
MAX_FILE = 262144


def digest(data):
    return hashlib.sha256(data).hexdigest()


def record_sync(db_path, execution_id):
    with sqlite3.connect(db_path) as conn:
        conn.execute("INSERT OR IGNORE INTO script_sync(execution_id) VALUES (?)", (execution_id,))


def complete_sync(db_path, execution_id):
    with sqlite3.connect(db_path) as conn:
        conn.execute("UPDATE script_sync SET completed=1 WHERE execution_id=?", (execution_id,))


def mark_started(db_path, execution_id):
    with sqlite3.connect(db_path) as conn:
        conn.execute("UPDATE script_sync SET started=1 WHERE execution_id=?", (execution_id,))


def machine_headers(settings, identity):
    return {"x-ringfour-credential-id": identity["credential_id"],
            "x-ringfour-credential-secret": Path(settings.credential_path).read_text().strip()}


def valid_bundle_path(name):
    return isinstance(name, str) and bool(name) and len(name) <= 240 and not name.startswith("/") and all(
        part not in ("", ".", "..") and re.fullmatch(r"[A-Za-z0-9_.-]+", part)
        for part in name.split("/")
    )


def valid_id(value):
    try:
        return isinstance(value, str) and str(uuid.UUID(value)) == value
    except (ValueError, AttributeError):
        return False


def validate_start_payload(payload):
    required = {"revision_id", "archive_sha256", "archive_size", "revision_digest", "entrypoint", "python_requirement", "lock_sha256"}
    if not isinstance(payload, dict) or set(payload) != required or not valid_id(payload["revision_id"]):
        raise runner.RunnerRejected("invalid_input", "Invalid revision metadata.")
    size, requirement = payload["archive_size"], payload["python_requirement"]
    if type(size) is not int or not 1 <= size <= MAX_ARCHIVE or not isinstance(requirement, str) or not requirement.strip() or len(requirement) > 128:
        raise runner.RunnerRejected("invalid_input", "Invalid revision metadata.")


def unpack_verified(archive_bytes, payload, stage_root):
    if any(not isinstance(payload.get(key), str) or not re.fullmatch(r"[0-9a-f]{64}", payload[key]) for key in ("archive_sha256", "revision_digest", "lock_sha256")):
        raise runner.RunnerRejected("invalid_input", "Invalid revision digest.")
    if not isinstance(payload.get("archive_size"), int) or not valid_bundle_path(payload.get("entrypoint", "")) or not payload["entrypoint"].endswith(".py"):
        raise runner.RunnerRejected("invalid_input", "Invalid revision metadata.")
    if len(archive_bytes) != payload["archive_size"] or len(archive_bytes) > MAX_ARCHIVE or digest(archive_bytes) != payload["archive_sha256"]:
        raise runner.RunnerRejected("invalid_input", "Downloaded revision failed verification.")
    files = {}
    directories = set()
    explicit = set()
    try:
        with zipfile.ZipFile(io.BytesIO(archive_bytes)) as archive:
            total = 0
            for entry in archive.infolist():
                directory = entry.is_dir()
                name = entry.filename[:-1] if directory else entry.filename
                file_type = (entry.external_attr >> 16) & 0o170000
                if not valid_bundle_path(name) or name in explicit or file_type not in (0, stat.S_IFDIR if directory else stat.S_IFREG):
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
                if name in directories or len(files) >= 1024 or entry.file_size > MAX_FILE:
                    raise ValueError("Invalid bundle size or path")
                with archive.open(entry) as source:
                    content = source.read(MAX_FILE + 1)
                total += len(content)
                if len(content) != entry.file_size or len(content) > MAX_FILE or total > MAX_ARCHIVE:
                    raise ValueError("Bundle size limit")
                files[name] = content
        if payload["entrypoint"] not in files or "requirements.lock" not in files:
            raise ValueError("Missing entrypoint or lock")
        if digest(files["requirements.lock"]) != payload["lock_sha256"]:
            raise ValueError("Lock digest mismatch")
        files["requirements.lock"].decode("utf-8")
        manifest = [[name, digest(data)] for name, data in sorted(files.items())]
        canonical = json.dumps(manifest, ensure_ascii=False, separators=(",", ":")).encode()
        if digest(canonical) != payload["revision_digest"]:
            raise ValueError("Bundle digest mismatch")
    except (ValueError, OSError, UnicodeError, zipfile.BadZipFile, KeyError) as exc:
        raise runner.RunnerRejected("invalid_input", "Downloaded revision failed verification.") from exc

    root = Path(stage_root) / "scripts"
    root.mkdir(parents=True, exist_ok=True)
    destination = root / payload["revision_digest"]
    if destination.exists():
        if destination.is_symlink() or not destination.is_dir():
            raise runner.RunnerRejected("invalid_input", "Staged revision is invalid.")
        actual = {}
        for path in destination.rglob("*"):
            if path.is_symlink() or (not path.is_file() and not path.is_dir()):
                raise runner.RunnerRejected("invalid_input", "Staged revision is invalid.")
            if path.is_file():
                with path.open("rb") as source:
                    actual[path.relative_to(destination).as_posix()] = source.read(MAX_FILE + 1)
        if actual != files:
            raise runner.RunnerRejected("invalid_input", "Staged revision does not match its digest.")
    else:
        temporary = Path(tempfile.mkdtemp(prefix=".stage-", dir=root))
        try:
            for name, data in files.items():
                target = temporary / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(data)
            os.replace(temporary, destination)
        finally:
            if temporary.exists():
                shutil.rmtree(temporary)
    return f"scripts/{payload['revision_digest']}"


async def start(settings, operation):
    validate_start_payload(operation.payload)
    identity = load_identity(settings.db_path)
    if identity is None or credential_error(settings.credential_path):
        raise runner.RunnerRejected("credential_missing", "Device credential unavailable.")
    headers = machine_headers(settings, identity)
    async with httpx.AsyncClient(base_url=identity["platform_url"], headers=headers, timeout=15) as client, \
            client.stream("GET", f"/v1/device/executions/{operation.operation_id}/revision") as response:
        response.raise_for_status()
        chunks = bytearray()
        async for chunk in response.aiter_bytes():
            chunks.extend(chunk)
            if len(chunks) > MAX_ARCHIVE:
                raise runner.RunnerRejected("invalid_input", "Downloaded revision exceeds its limit.")
    code_ref = unpack_verified(bytes(chunks), operation.payload, settings.stage_dir)
    record_sync(settings.db_path, operation.operation_id)
    body = {"execution_id": operation.operation_id, "robot_id": operation.robot_id,
            "revision_digest": operation.payload["revision_digest"], "code_ref": code_ref,
            "entrypoint": operation.payload["entrypoint"], "python_requirement": operation.payload["python_requirement"],
            "lock_sha256": operation.payload["lock_sha256"]}
    async with httpx.AsyncClient(base_url=settings.runner_url, headers={"authorization": f"Bearer {settings.runner_auth_token}"}, timeout=10) as client:
        response = await client.post("/v1/executions", json=body)
        if response.status_code not in (200, 201):
            complete_sync(settings.db_path, operation.operation_id)
            runner.reject_response(response)
        mark_started(settings.db_path, operation.operation_id)
        return {"execution": response.json()}


async def reconcile_start(settings, operation):
    """Recover an ambiguous start by querying its stable Runner execution ID."""
    async with httpx.AsyncClient(base_url=settings.runner_url, headers={"authorization": f"Bearer {settings.runner_auth_token}"}, timeout=10) as client:
        response = await client.get(f"/v1/executions/{operation.operation_id}")
        if response.status_code == 404:
            return None
        if response.status_code != 200:
            runner.reject_response(response)
        record_sync(settings.db_path, operation.operation_id)
        mark_started(settings.db_path, operation.operation_id)
        return {"execution": response.json()}


async def stop(settings, operation):
    if not isinstance(operation.payload, dict) or set(operation.payload) != {"execution_id"} or not valid_id(operation.payload["execution_id"]):
        raise runner.RunnerRejected("invalid_input", "Invalid stop request.")
    async with httpx.AsyncClient(base_url=settings.runner_url, headers={"authorization": f"Bearer {settings.runner_auth_token}"}, timeout=8) as client:
        response = await client.post(f"/v1/executions/{operation.payload['execution_id']}/stop")
        if response.status_code != 200:
            runner.reject_response(response)
        return {"execution": response.json()}


def sync_rows(db_path):
    with sqlite3.connect(db_path) as conn:
        return conn.execute("SELECT execution_id,stdout_offset,stderr_offset,started FROM script_sync WHERE completed=0 ORDER BY rowid").fetchall()


def advance(db_path, execution_id, stream, offset):
    column = "stdout_offset" if stream == "stdout" else "stderr_offset"
    with sqlite3.connect(db_path) as conn:
        conn.execute(f"UPDATE script_sync SET {column}=? WHERE execution_id=?", (offset, execution_id))


async def synchronize(settings, identity, row):
    execution_id, stdout_offset, stderr_offset, started = row
    machine = machine_headers(settings, identity)
    runner_headers = {"authorization": f"Bearer {settings.runner_auth_token}"}
    async with httpx.AsyncClient(base_url=settings.runner_url, headers=runner_headers, timeout=10) as local, \
            httpx.AsyncClient(base_url=identity["platform_url"], headers=machine, timeout=15) as platform:
        response = await local.get(f"/v1/executions/{execution_id}")
        if response.status_code == 404:
            if started:
                status = {"state": "failed", "exit_code": None,
                    "error": {"code": "interrupted_unknown_outcome", "message": "Runner lost the recorded execution."},
                    "stdout_truncated": False, "stderr_truncated": False}
                response = await platform.put(f"/v1/device/executions/{execution_id}/status", json=status)
                response.raise_for_status()
                complete_sync(settings.db_path, execution_id)
            return  # Before start is confirmed, operation replay owns starting it.
        response.raise_for_status()
        snapshot = response.json()
        truncation = {}
        for stream, offset in (("stdout", stdout_offset), ("stderr", stderr_offset)):
            while True:
                response = await local.get(f"/v1/executions/{execution_id}/output", params={"stream": stream, "offset": offset, "limit": 16384})
                response.raise_for_status()
                chunk = response.json()
                data = base64.b64decode(chunk["data_base64"], validate=True)
                if chunk["offset"] != offset or chunk["next_offset"] != offset + len(data):
                    raise ValueError("Invalid Runner output offsets")
                truncation[stream] = chunk["truncated"]
                if not data:
                    break
                response = await platform.put(f"/v1/device/executions/{execution_id}/output/{stream}", params={"offset": offset}, content=data)
                response.raise_for_status()
                offset += len(data)
                advance(settings.db_path, execution_id, stream, offset)
        terminal = snapshot["state"] in ("succeeded", "failed", "cancelled")
        if terminal:
            response = await local.get(f"/v1/executions/{execution_id}/artifacts")
            response.raise_for_status()
            for artifact in response.json()["artifacts"]:
                data = bytearray()
                while len(data) < artifact["size"]:
                    response = await local.get(f"/v1/executions/{execution_id}/artifacts/content", params={"path": artifact["path"], "offset": len(data), "limit": 16384})
                    response.raise_for_status()
                    chunk = response.json()
                    part = base64.b64decode(chunk["data_base64"], validate=True)
                    if not part or chunk["offset"] != len(data) or chunk["next_offset"] != len(data) + len(part):
                        raise ValueError("Invalid Runner artifact offsets")
                    data.extend(part)
                    if len(data) > MAX_FILE:
                        raise ValueError("Runner artifact exceeds limit")
                if len(data) != artifact["size"] or digest(data) != artifact["sha256"]:
                    raise ValueError("Runner artifact digest mismatch")
                response = await platform.put(f"/v1/device/executions/{execution_id}/artifacts", params={"path": artifact["path"], "sha256": artifact["sha256"]}, content=bytes(data))
                response.raise_for_status()
        status = {"state": snapshot["state"], "exit_code": snapshot["exit_code"], "error": snapshot["error"],
                  "stdout_truncated": truncation["stdout"], "stderr_truncated": truncation["stderr"]}
        response = await platform.put(f"/v1/device/executions/{execution_id}/status", json=status)
        response.raise_for_status()
        if terminal:
            complete_sync(settings.db_path, execution_id)


async def run_sync(settings):
    while True:
        try:
            identity = load_identity(settings.db_path)
            if identity is not None and not credential_error(settings.credential_path) and settings.runner_auth_token:
                for row in sync_rows(settings.db_path):
                    try:
                        await synchronize(settings, identity, row)
                    except (httpx.HTTPError, OSError, ValueError, KeyError, TypeError, sqlite3.Error):
                        logger.warning("script synchronization failed for %s; will retry", row[0])
        except (sqlite3.Error, OSError):
            logger.warning("script synchronization storage unavailable; will retry")
        await asyncio.sleep(2)
