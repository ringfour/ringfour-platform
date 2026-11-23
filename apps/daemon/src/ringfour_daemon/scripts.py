"""Stage verified scripts and replicate Runner output through durable offsets."""
import asyncio
import base64
import codecs
import errno
import hashlib
import json
import logging
import os
import re
import shutil
import sqlite3
import stat
import tempfile
import time
import uuid
import zipfile
from pathlib import Path

import httpx

from . import runner
from .db import connect
from .enrollment import credential_error, load_identity

logger = logging.getLogger(__name__)
MAX_EXACT_SIZE = 9007199254740991
MIN_FREE_BYTES = 268435456


def cleanup_interrupted_stages(stage_root):
    """Remove old unpublished extraction directories; published revisions remain untouched."""
    root = Path(stage_root) / "scripts"
    if not root.is_dir() or root.is_symlink():
        return
    cutoff = time.time() - 48 * 60 * 60
    for candidate in root.glob(".stage-*"):
        try:
            if not candidate.is_symlink() and candidate.is_dir() and candidate.stat().st_mtime < cutoff:
                shutil.rmtree(candidate)
        except OSError:
            logger.warning("Unable to clean an interrupted mission stage")


def digest(data):
    return hashlib.sha256(data).hexdigest()


def record_sync(db_path, execution_id):
    with connect(db_path) as conn:
        conn.execute("INSERT OR IGNORE INTO script_sync(execution_id) VALUES (?)", (execution_id,))


def complete_sync(db_path, execution_id):
    with connect(db_path) as conn:
        conn.execute("UPDATE script_sync SET completed=1 WHERE execution_id=?", (execution_id,))


def mark_started(db_path, execution_id):
    with connect(db_path) as conn:
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
    if (not isinstance(payload, dict) or set(payload) not in (required, required | {"mission_revision_id"})
            or not valid_id(payload["revision_id"])
            or ("mission_revision_id" in payload and not valid_id(payload["mission_revision_id"]))):
        raise runner.RunnerRejected("invalid_input", "Invalid revision metadata.")
    size, requirement = payload["archive_size"], payload["python_requirement"]
    if type(size) is not int or not 1 <= size <= MAX_EXACT_SIZE or not isinstance(requirement, str) or not requirement.strip() or len(requirement) > 128:
        raise runner.RunnerRejected("invalid_input", "Invalid revision metadata.")


def unpack_verified(archive_bytes, payload, stage_root):
    """Compatibility entry point for bounded in-memory fixtures."""
    with tempfile.TemporaryFile() as archive:
        archive.write(archive_bytes)
        archive.flush()
        return unpack_verified_file(archive, payload, stage_root)


def unpack_verified_file(archive_file, payload, stage_root):
    if any(not isinstance(payload.get(key), str) or not re.fullmatch(r"[0-9a-f]{64}", payload[key]) for key in ("archive_sha256", "revision_digest", "lock_sha256")):
        raise runner.RunnerRejected("invalid_input", "Invalid revision digest.")
    if type(payload.get("archive_size")) is not int or not valid_bundle_path(payload.get("entrypoint", "")) or not payload["entrypoint"].endswith(".py"):
        raise runner.RunnerRejected("invalid_input", "Invalid revision metadata.")
    archive_file.seek(0, os.SEEK_END)
    size = archive_file.tell()
    archive_file.seek(0)
    if size != payload["archive_size"] or size > MAX_EXACT_SIZE or hashlib.file_digest(archive_file, "sha256").hexdigest() != payload["archive_sha256"]:
        raise runner.RunnerRejected("invalid_input", "Downloaded revision failed verification.")
    archive_file.seek(0)
    root = Path(stage_root) / "scripts"
    root.mkdir(parents=True, exist_ok=True)
    if Path(stage_root).is_symlink() or root.is_symlink():
        raise runner.RunnerRejected("invalid_input", "Staging directory is invalid.")
    destination = root / payload["revision_digest"]
    temporary = Path(tempfile.mkdtemp(prefix=".stage-", dir=root))
    files, directories, explicit = {}, set(), set()
    try:
        with zipfile.ZipFile(archive_file) as archive:
            if len(archive.infolist()) > 2048:
                raise ValueError("Too many entries")
            total = 0
            available = max(0, shutil.disk_usage(root).free - MIN_FREE_BYTES)
            announced = sum(entry.file_size for entry in archive.infolist() if not entry.is_dir())
            if announced > available:
                raise runner.RunnerRejected("insufficient_storage", "Not enough robot storage to extract this mission.")
            next_report = 64 * 1024 * 1024
            next_space_check = 0
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
                if name in directories or len(files) >= 1024 or entry.file_size > MAX_EXACT_SIZE:
                    raise ValueError("Invalid bundle size or path")
                target = temporary.joinpath(*components)
                target.parent.mkdir(parents=True, exist_ok=True)
                checksum = hashlib.sha256()
                decoder = codecs.getincrementaldecoder("utf-8")("strict") if name == "requirements.lock" else None
                length = 0
                with archive.open(entry) as source, target.open("xb") as output:
                    while chunk := source.read(65536):
                        length += len(chunk)
                        total += len(chunk)
                        if length > entry.file_size or total > available:
                            raise ValueError("Bundle size limit")
                        if total >= next_space_check:
                            if shutil.disk_usage(root).free < MIN_FREE_BYTES + len(chunk):
                                raise runner.RunnerRejected("insufficient_storage", "Not enough robot storage to extract this mission.")
                            next_space_check = total + 16 * 1024 * 1024
                        checksum.update(chunk)
                        if decoder is not None:
                            decoder.decode(chunk)
                        output.write(chunk)
                        if total >= next_report:
                            logger.info("Mission preparation: extracted %d MiB", total // 1048576)
                            next_report = total + 64 * 1024 * 1024
                    if decoder is not None:
                        decoder.decode(b"", final=True)
                if length != entry.file_size:
                    raise ValueError("Archive length mismatch")
                files[name] = (length, checksum.hexdigest())
        if payload["entrypoint"] not in files or "requirements.lock" not in files or files["requirements.lock"][1] != payload["lock_sha256"]:
            raise ValueError("Missing entrypoint or lock")
        manifest = [[name, value[1]] for name, value in sorted(files.items())]
        if digest(json.dumps(manifest, ensure_ascii=False, separators=(",", ":")).encode()) != payload["revision_digest"]:
            raise ValueError("Bundle digest mismatch")
        if destination.exists():
            if destination.is_symlink() or not destination.is_dir():
                raise ValueError("Staged revision is invalid")
            actual = {}
            for path in destination.rglob("*"):
                if path.is_symlink() or (not path.is_file() and not path.is_dir()):
                    raise ValueError("Staged revision is invalid")
                if path.is_file():
                    with path.open("rb") as source:
                        actual[path.relative_to(destination).as_posix()] = (path.stat().st_size, hashlib.file_digest(source, "sha256").hexdigest())
            if actual != files:
                raise ValueError("Staged revision does not match its digest")
        else:
            os.replace(temporary, destination)
        return f"scripts/{payload['revision_digest']}"
    except OSError as exc:
        if exc.errno in (errno.ENOSPC, errno.EDQUOT):
            raise runner.RunnerRejected("insufficient_storage", "Not enough robot storage to extract this mission.") from exc
        raise runner.RunnerRejected("invalid_input", "Downloaded revision failed verification.") from exc
    except (ValueError, UnicodeError, zipfile.BadZipFile, KeyError, RuntimeError) as exc:
        raise runner.RunnerRejected("invalid_input", "Downloaded revision failed verification.") from exc
    finally:
        if temporary.exists():
            shutil.rmtree(temporary)


async def download_and_stage(response, payload, stage_root):
    """Stream into an anonymous temporary file, then verify and stage off the event loop."""
    if shutil.disk_usage(tempfile.gettempdir()).free < payload["archive_size"] + MIN_FREE_BYTES:
        raise runner.RunnerRejected("insufficient_storage", "Not enough temporary storage to download this mission.")
    try:
        with tempfile.TemporaryFile() as archive:
            total = 0
            next_report = 64 * 1024 * 1024
            async for chunk in response.aiter_bytes(65536):
                total += len(chunk)
                if total > payload["archive_size"]:
                    raise runner.RunnerRejected("invalid_input", "Downloaded revision exceeds its declared size.")
                await asyncio.to_thread(archive.write, chunk)
                if total >= next_report:
                    logger.info("Mission download: %d of %d MiB", total // 1048576, payload["archive_size"] // 1048576)
                    next_report = total + 64 * 1024 * 1024
            await asyncio.to_thread(archive.flush)
            return await asyncio.to_thread(unpack_verified_file, archive, payload, stage_root)
    except OSError as exc:
        if exc.errno in (errno.ENOSPC, errno.EDQUOT):
            raise runner.RunnerRejected("insufficient_storage", "Not enough temporary storage to download this mission.") from exc
        raise


async def start(settings, operation):
    validate_start_payload(operation.payload)
    identity = load_identity(settings.db_path)
    if identity is None or credential_error(settings.credential_path):
        raise runner.RunnerRejected("credential_missing", "Device credential unavailable.")
    headers = machine_headers(settings, identity)
    async with httpx.AsyncClient(base_url=identity["platform_url"], headers=headers, timeout=httpx.Timeout(None, connect=15, write=300)) as client, \
            client.stream("GET", f"/v1/device/executions/{operation.operation_id}/revision") as response:
        response.raise_for_status()
        code_ref = await download_and_stage(response, operation.payload, settings.stage_dir)
    record_sync(settings.db_path, operation.operation_id)
    body = {"execution_id": operation.operation_id, "robot_id": operation.robot_id,
            "revision_digest": operation.payload["revision_digest"], "code_ref": code_ref,
            "entrypoint": operation.payload["entrypoint"], "python_requirement": operation.payload["python_requirement"],
            "lock_sha256": operation.payload["lock_sha256"]}
    async with httpx.AsyncClient(base_url=settings.runner_url, headers={"authorization": f"Bearer {settings.runner_auth_token}"}, timeout=httpx.Timeout(None, connect=15, write=300)) as client:
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


async def prepare_mission(settings, operation):
    payload = operation.payload
    expected = {"deployment_id", "mission_revision_id", "revision_id", "archive_sha256", "archive_size", "revision_digest", "entrypoint", "python_requirement", "lock_sha256", "retain_digests"}
    if not isinstance(payload, dict) or set(payload) != expected or not valid_id(payload["deployment_id"]) or not valid_id(payload["mission_revision_id"]):
        raise runner.RunnerRejected("invalid_input", "Invalid mission candidate.")
    validate_start_payload({key: payload[key] for key in ("revision_id", "archive_sha256", "archive_size", "revision_digest", "entrypoint", "python_requirement", "lock_sha256")})
    retain = payload["retain_digests"]
    if not isinstance(retain, list) or len(retain) > 2 or any(not isinstance(item, str) or not re.fullmatch(r"[0-9a-f]{64}", item) for item in retain) or len(set(retain)) != len(retain):
        raise runner.RunnerRejected("invalid_input", "Invalid retained revisions.")
    identity = load_identity(settings.db_path)
    if identity is None or credential_error(settings.credential_path):
        raise runner.RunnerRejected("credential_missing", "Device credential unavailable.")
    async with httpx.AsyncClient(base_url=identity["platform_url"], headers=machine_headers(settings, identity), timeout=httpx.Timeout(None, connect=15, write=300)) as client, \
            client.stream("GET", f"/v1/device/deployments/{payload['deployment_id']}/candidate", params={"operation_id": operation.operation_id}) as response:
        response.raise_for_status()
        code_ref = await download_and_stage(response, payload, settings.stage_dir)
    request = {"revision_digest": payload["revision_digest"], "code_ref": code_ref, "entrypoint": payload["entrypoint"],
               "python_requirement": payload["python_requirement"], "lock_sha256": payload["lock_sha256"], "retain_digests": retain}
    async with httpx.AsyncClient(base_url=settings.runner_url, headers={"authorization": f"Bearer {settings.runner_auth_token}"}, timeout=httpx.Timeout(None, connect=15, write=300)) as client:
        deadline = time.monotonic() + 86400
        while True:
            response = await client.post("/v1/revisions/prepare", json=request)
            try:
                preparing = response.status_code == 409 and response.json().get("error", {}).get("code") == "preparation_in_progress"
            except ValueError:
                preparing = False
            if not preparing or time.monotonic() >= deadline:
                break
            await asyncio.sleep(2)
        if response.status_code != 200:
            runner.reject_response(response)
        result = response.json()
        if result != {"revision_digest": payload["revision_digest"], "state": "ready"}:
            raise runner.RunnerRejected("runner_error", "Runner returned an invalid preparation result.")
        return result


def sync_rows(db_path):
    with connect(db_path) as conn:
        return conn.execute("SELECT execution_id,stdout_offset,stderr_offset,started FROM script_sync WHERE completed=0 ORDER BY rowid").fetchall()


def advance(db_path, execution_id, stream, offset):
    column = "stdout_offset" if stream == "stdout" else "stderr_offset"
    with connect(db_path) as conn:
        conn.execute(f"UPDATE script_sync SET {column}=? WHERE execution_id=?", (offset, execution_id))


async def synchronize(settings, identity, row):
    execution_id, stdout_offset, stderr_offset, started = row
    machine = machine_headers(settings, identity)
    runner_headers = {"authorization": f"Bearer {settings.runner_auth_token}"}
    transfer_timeout = httpx.Timeout(None, connect=15, write=300)
    async with httpx.AsyncClient(base_url=settings.runner_url, headers=runner_headers, timeout=transfer_timeout) as local, \
            httpx.AsyncClient(base_url=identity["platform_url"], headers=machine, timeout=transfer_timeout) as platform:
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
        status = {"state": snapshot["state"], "exit_code": snapshot["exit_code"], "error": snapshot["error"],
                  "stdout_truncated": truncation["stdout"], "stderr_truncated": truncation["stderr"]}
        response = await platform.put(f"/v1/device/executions/{execution_id}/status", json=status)
        response.raise_for_status()
        if terminal:
            async def artifact_status(state, failure=None):
                response = await platform.put(f"/v1/device/executions/{execution_id}/artifact-status", json={"state": state, "error": failure})
                response.raise_for_status()

            response = await local.get(f"/v1/executions/{execution_id}/artifacts")
            response.raise_for_status()
            artifact_result = response.json()
            if artifact_result.get("artifact_state") == "failed":
                await artifact_status("failed", artifact_result.get("artifact_error") or {"code": "artifact_collection_failed", "message": "Artifact collection failed."})
                complete_sync(settings.db_path, execution_id)
                return
            await artifact_status("uploading")
            try:
                await upload_artifacts(local, platform, execution_id, artifact_result["artifacts"])
            except (httpx.HTTPError, OSError, ValueError, KeyError, TypeError):
                try:
                    await artifact_status("failed", {"code": "artifact_upload_failed", "message": "Artifact delivery failed and will be retried."})
                except httpx.HTTPError:
                    logger.warning("could not report artifact delivery failure for %s", execution_id)
                raise
            if artifact_result.get("artifact_state") == "ready":
                await artifact_status("ready")
            else:
                await artifact_status("failed", {"code": "artifact_status_unknown", "message": "Runner could not verify artifact collection."})
            complete_sync(settings.db_path, execution_id)


async def upload_artifacts(local, platform, execution_id, artifacts):
    for artifact in artifacts:
        if type(artifact.get("size")) is not int or not 0 <= artifact["size"] <= MAX_EXACT_SIZE:
            raise ValueError("Invalid artifact size")
        if shutil.disk_usage(tempfile.gettempdir()).free < artifact["size"] + MIN_FREE_BYTES:
            raise OSError("Insufficient temporary storage for artifact")
        with tempfile.TemporaryFile() as data:
            checksum = hashlib.sha256()
            offset = 0
            async with local.stream("GET", f"/v1/executions/{execution_id}/artifacts/download", params={"path": artifact["path"]}) as download:
                download.raise_for_status()
                async for part in download.aiter_bytes(65536):
                    offset += len(part)
                    if offset > artifact["size"]:
                        raise ValueError("Runner artifact exceeds limit")
                    checksum.update(part)
                    await asyncio.to_thread(data.write, part)
            if offset != artifact["size"] or checksum.hexdigest() != artifact["sha256"]:
                raise ValueError("Runner artifact digest mismatch")
            await asyncio.to_thread(data.flush)
            data.seek(0)

            async def artifact_chunks():
                while part := await asyncio.to_thread(data.read, 65536):
                    yield part

            response = await platform.put(f"/v1/device/executions/{execution_id}/artifacts", params={"path": artifact["path"], "sha256": artifact["sha256"]},
                headers={"Content-Length": str(offset)}, content=artifact_chunks())
            response.raise_for_status()

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
