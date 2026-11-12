"""Authenticated, atomic candidate environment preparation and cache GC."""
import asyncio
import hashlib
import json
import os
import re
import shutil
import signal
import sys
import tempfile
import uuid
from pathlib import Path

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse
from packaging.specifiers import InvalidSpecifier, SpecifierSet
from packaging.version import Version

router = APIRouter()
MAX_PREPARED_DIGESTS = 20
_DIGEST = re.compile(r"[0-9a-f]{64}")
_GENERATION = re.compile(r"[0-9a-f]{32}")


def prepared_environment(settings, digest, requirement, lock_sha256):
    """Return a published matching environment, or None if it is absent/stale."""
    if not isinstance(digest, str) or not _DIGEST.fullmatch(digest):
        return None
    marker = Path(settings.data_dir) / "prepared" / f"{digest}.json"
    try:
        if marker.is_symlink():
            return None
        value = json.loads(marker.read_text())
        generation = value["generation"]
        if not isinstance(generation, str) or not _GENERATION.fullmatch(generation):
            return None
        if value["requirement"] != requirement or value["lock_sha256"] != lock_sha256 or value["python"] != sys.version.split()[0]:
            return None
        environment = Path(settings.data_dir) / "prepared-envs" / digest / generation
        if environment.is_symlink() or not (environment / "bin" / "python").exists():
            return None
        return environment
    except (OSError, ValueError, KeyError, TypeError):
        return None


async def _command(app, *args):
    proc = await asyncio.create_subprocess_exec(*args, stdout=asyncio.subprocess.DEVNULL,
        stderr=asyncio.subprocess.DEVNULL, start_new_session=True)
    key = "prepare-" + uuid.uuid4().hex
    app.state.processes[key] = proc
    try:
        return await proc.wait()
    except asyncio.CancelledError:
        try:
            os.killpg(proc.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        await proc.wait()
        raise
    finally:
        app.state.processes.pop(key, None)


def collect_cache(settings, retain):
    """Keep recently used prepared digests while preserving protected ones."""
    root = Path(settings.data_dir) / "prepared-envs"
    markers = Path(settings.data_dir) / "prepared"
    if not root.exists():
        return
    candidates = [path for path in root.iterdir() if not path.is_symlink() and path.is_dir() and _DIGEST.fullmatch(path.name)]
    count = len(candidates)
    for path in sorted(candidates, key=lambda item: item.stat().st_mtime):
        if count <= MAX_PREPARED_DIGESTS:
            break
        if path.name in retain:
            continue
        marker = markers / f"{path.name}.json"
        marker.unlink(missing_ok=True)
        shutil.rmtree(path)
        count -= 1


@router.post("/v1/revisions/prepare")
async def prepare(request: Request):
    # Imported here to keep the existing execution router independently usable.
    from .executions import authorize, bundle_files, connection, error

    if not authorize(request):
        return error(401, "unauthenticated", "Runner authentication required.")
    try:
        raw = bytearray()
        async for chunk in request.stream():
            raw.extend(chunk)
            if len(raw) > 4096:
                return error(413, "size_limit", "Prepare request exceeds 4096 bytes.")
        body = json.loads(raw)
        expected = {"revision_digest", "code_ref", "entrypoint", "python_requirement", "lock_sha256", "retain_digests"}
        if not isinstance(body, dict) or set(body) != expected:
            raise ValueError("Invalid prepare request")
        digest, lock, requirement, retain = (body[key] for key in ("revision_digest", "lock_sha256", "python_requirement", "retain_digests"))
        if not isinstance(digest, str) or not _DIGEST.fullmatch(digest) or not isinstance(lock, str) or not _DIGEST.fullmatch(lock):
            raise ValueError("Invalid digest")
        if not isinstance(requirement, str) or not 1 <= len(requirement) <= 128 or Version(".".join(map(str, sys.version_info[:3]))) not in SpecifierSet(requirement):
            raise ValueError("Incompatible Python")
        if not isinstance(retain, list) or len(retain) > 2 or any(not isinstance(item, str) or not _DIGEST.fullmatch(item) for item in retain) or len(set(retain)) != len(retain):
            raise ValueError("Invalid retained digests")
        files, actual = bundle_files(request.app.state.settings, body["code_ref"], body["entrypoint"])
        lock_bytes = next((data for name, data in files if name == "requirements.lock"), None)
        if actual != digest or lock_bytes is None or hashlib.sha256(lock_bytes).hexdigest() != lock:
            raise ValueError("Staged bundle mismatch")
        lock_bytes.decode("utf-8")
    except (ValueError, OSError, UnicodeError, InvalidSpecifier):
        return error(400, "invalid_input", "Invalid staged revision or prepare request.")
    app = request.app
    if app.state.prepare_lock.locked():
        return error(409, "preparation_in_progress", "Runner is already preparing a revision.")
    async with app.state.prepare_lock:
        with connection(app.state.settings) as conn:
            if conn.execute("SELECT 1 FROM executions WHERE state IN ('queued','preparing','running') LIMIT 1").fetchone():
                return error(409, "execution_conflict", "Another execution is active.")
        settings = app.state.settings
        existing = prepared_environment(settings, digest, requirement, lock)
        if existing is not None:
            os.utime(existing.parent, None)
            collect_cache(settings, set(retain) | {digest})
            return {"revision_digest": digest, "state": "ready"}
        root = Path(settings.data_dir)
        generation = uuid.uuid4().hex
        parent = root / "prepared-envs" / digest
        environment = parent / generation
        parent.mkdir(parents=True, exist_ok=True)
        temp_source = Path(tempfile.mkdtemp(prefix="candidate-", dir=root))
        published = False
        try:
            for name, data in files:
                target = temp_source / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(data)
                if hashlib.sha256(target.read_bytes()).digest() != hashlib.sha256(data).digest():
                    raise OSError("Candidate copy failed verification")
            if await _command(app, "uv", "venv", "--no-python-downloads", "--python", sys.executable, str(environment)):
                return error(503, "preparation_failed", "Unable to prepare the candidate environment.")
            if await _command(app, "uv", "pip", "sync", "--require-hashes", "--python", str(environment / "bin" / "python"), str(temp_source / "requirements.lock")):
                return error(503, "preparation_failed", "Unable to install the candidate lock.")
            markers = root / "prepared"
            markers.mkdir(exist_ok=True)
            marker = markers / f"{digest}.json"
            temporary = markers / f".{digest}.{generation}.tmp"
            temporary.write_text(json.dumps({"generation": generation, "requirement": requirement,
                "lock_sha256": lock, "python": sys.version.split()[0]}, separators=(",", ":")))
            temporary.replace(marker)
            published = True
            os.utime(parent, None)
            for prior in parent.iterdir():
                if prior.name != generation and _GENERATION.fullmatch(prior.name) and not prior.is_symlink() and prior.is_dir():
                    shutil.rmtree(prior)
            collect_cache(settings, set(retain) | {digest})
            return JSONResponse({"revision_digest": digest, "state": "ready"})
        except OSError:
            return error(503, "preparation_failed", "Unable to prepare the candidate environment.")
        finally:
            shutil.rmtree(temp_source, ignore_errors=True)
            if not published:
                shutil.rmtree(environment, ignore_errors=True)
