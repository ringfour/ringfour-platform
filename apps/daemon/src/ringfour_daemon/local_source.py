"""Local sync staging and durable metadata outbox; source never visits Platform."""
import asyncio
import base64
import binascii
import hashlib
import json
import logging
import os
import sqlite3
import tempfile
import threading
import time
import uuid
from pathlib import Path

import httpx

from . import source_validation
from .enrollment import credential_error, load_identity
from .scripts import machine_headers

logger = logging.getLogger(__name__)
_GUARD = threading.Lock()
MAX_BODY = 3145728


def _hash(data):
    return hashlib.sha256(data).hexdigest()


def _row(conn, operation_id):
    conn.row_factory = sqlite3.Row
    return conn.execute("SELECT * FROM local_source_syncs WHERE operation_id=?", (operation_id,)).fetchone()


def prepare(settings, session_id, grant_id, key, body):
    """Validate bytes independently; persist a stable pending operation."""
    if not isinstance(key, str) or not key.strip() or len(key) > 200:
        raise ValueError("invalid_key")
    expected = {"base_generation", "archive_sha256", "archive_size", "archive_base64", "manifest"}
    if not isinstance(body, dict) or set(body) != expected or type(body["base_generation"]) is not int or body["base_generation"] < 0:
        raise ValueError("invalid_source")
    if ((type(body["archive_size"]) is int and body["archive_size"] > source_validation.MAX_ARCHIVE)
            or (isinstance(body["archive_base64"], str) and len(body["archive_base64"]) > 1398104)):
        raise ValueError("source_too_large")
    try:
        if not isinstance(body["archive_base64"], str):
            raise TypeError
        archive = base64.b64decode(body["archive_base64"], validate=True)
        if (base64.b64encode(archive).decode() != body["archive_base64"] or type(body["archive_size"]) is not int
                or not 1 <= body["archive_size"] <= source_validation.MAX_ARCHIVE or len(archive) != body["archive_size"]
                or not isinstance(body["archive_sha256"], str) or _hash(archive) != body["archive_sha256"]):
            raise ValueError
        files = source_validation.validate_snapshot(archive, body["manifest"])
    except (ValueError, TypeError, binascii.Error):
        raise ValueError("invalid_source") from None
    fingerprint = _hash(json.dumps(body, sort_keys=True, separators=(",", ":")).encode())
    key_hash = _hash(key.encode())
    with sqlite3.connect(settings.db_path) as conn:
        conn.row_factory = sqlite3.Row
        conn.execute("BEGIN IMMEDIATE")
        session = conn.execute("SELECT state FROM local_dev_sessions WHERE session_id=?", (session_id,)).fetchone()
        if not session or session["state"] != "open":
            raise ValueError("session_unavailable")
        old = conn.execute("SELECT * FROM local_source_syncs WHERE session_id=? AND key_hash=?", (session_id, key_hash)).fetchone()
        if old:
            if old["request_hash"] != fingerprint:
                raise ValueError("conflict")
            return old["operation_id"]
        pending = conn.execute("SELECT 1 FROM local_source_syncs WHERE session_id=? AND state='pending'", (session_id,)).fetchone()
        if pending:
            raise ValueError("sync_in_progress")
        previous = conn.execute("SELECT generation,manifest FROM local_source_syncs WHERE session_id=? AND state IN ('committed','reported') ORDER BY generation DESC LIMIT 1", (session_id,)).fetchone()
        generation = previous["generation"] if previous else 0
        if body["base_generation"] != generation:
            raise ValueError("stale_generation")
        if generation >= 20:
            raise ValueError("capacity_exceeded")
        prior_paths = {item["path"] for item in json.loads(previous["manifest"])["files"]} if previous else set()
        if set(body["manifest"]["deleted_paths"]) != prior_paths - files.keys():
            raise ValueError("invalid_source")
        operation_id = str(uuid.uuid4())
        ref = f"dev-sync/{session_id}/{operation_id}.zip"
        stage_root = Path(settings.stage_dir)
        sync_root = stage_root / "dev-sync"
        directory = sync_root / session_id
        if stage_root.is_symlink() or sync_root.is_symlink() or directory.is_symlink():
            raise ValueError("invalid_source")
        directory.mkdir(parents=True, exist_ok=True)
        fd, temp = tempfile.mkstemp(prefix=".source-", dir=directory)
        target = directory / f"{operation_id}.zip"
        try:
            os.fchmod(fd, 0o600)
            with os.fdopen(fd, "wb") as handle:
                handle.write(archive)
                handle.flush()
                os.fsync(handle.fileno())
            os.replace(temp, target)
            dir_fd = os.open(directory, os.O_RDONLY)
            try:
                os.fsync(dir_fd)
            finally:
                os.close(dir_fd)
            conn.execute("INSERT INTO local_source_syncs VALUES (?,?,?,?,?,?,?,?,?,?,?,'pending',NULL)",
                (operation_id, session_id, key_hash, fingerprint, grant_id, generation, generation + 1,
                 body["archive_sha256"], body["archive_size"], json.dumps(body["manifest"], sort_keys=True, separators=(",", ":")), ref))
            conn.commit()
        except BaseException:
            Path(temp).unlink(missing_ok=True)
            target.unlink(missing_ok=True)
            raise
    return operation_id


async def process(settings, operation_id):
    """Commit a pending operation at Runner; a lost response reuses its ID."""
    if not _GUARD.acquire(blocking=False):
        return None
    try:
        with sqlite3.connect(settings.db_path) as conn:
            row = _row(conn, operation_id)
            if not row:
                return None
            record = dict(row)
            session = conn.execute("SELECT state FROM local_dev_sessions WHERE session_id=?", (record["session_id"],)).fetchone()
            if record["state"] == "pending" and (not session or session[0] != "open"):
                conn.execute("UPDATE local_source_syncs SET state='cancelled' WHERE operation_id=?", (operation_id,))
                (Path(settings.stage_dir) / record["staged_archive"]).unlink(missing_ok=True)
                return {"error": "session_unavailable"}
        if record["state"] in ("committed", "reported"):
            (Path(settings.stage_dir) / record["staged_archive"]).unlink(missing_ok=True)
            return {"session_id": record["session_id"], "generation": record["generation"], "archive_sha256": record["archive_sha256"]}
        if record["state"] in ("failed", "cancelled"):
            return {"error": record["error_code"] or "session_unavailable"}
        if not settings.runner_auth_token:
            return None
        payload = {"operation_id": operation_id, "session_id": record["session_id"], "base_generation": record["base_generation"],
                   "generation": record["generation"], "archive_sha256": record["archive_sha256"], "archive_size": record["archive_size"],
                   "staged_archive": record["staged_archive"], "manifest": json.loads(record["manifest"])}
        try:
            async with httpx.AsyncClient(base_url=settings.runner_url, headers={"authorization": f"Bearer {settings.runner_auth_token}"}, timeout=20) as client:
                response = await client.post("/v1/dev-sessions/sync", json=payload)
                if response.status_code >= 500:
                    return None
                if response.status_code != 200:
                    try:
                        code = response.json()["error"]["code"]
                    except (KeyError, ValueError, TypeError):
                        code = "invalid_source"
                    if code not in {"invalid_source", "stale_generation", "capacity_exceeded", "session_unavailable", "conflict"}:
                        code = "invalid_source"
                    with sqlite3.connect(settings.db_path) as conn:
                        conn.execute("UPDATE local_source_syncs SET state='failed',error_code=? WHERE operation_id=? AND state='pending'", (code, operation_id))
                    (Path(settings.stage_dir) / record["staged_archive"]).unlink(missing_ok=True)
                    return {"error": code}
                value = response.json()
        except (httpx.HTTPError, ValueError):
            return None
        expected = {"session_id": record["session_id"], "generation": record["generation"], "archive_sha256": record["archive_sha256"]}
        if value != expected:
            return None
        with sqlite3.connect(settings.db_path) as conn:
            conn.execute("UPDATE local_source_syncs SET state='committed' WHERE operation_id=? AND state='pending'", (operation_id,))
        (Path(settings.stage_dir) / record["staged_archive"]).unlink(missing_ok=True)
        return expected
    finally:
        _GUARD.release()


async def report(settings, operation_id):
    with sqlite3.connect(settings.db_path) as conn:
        row = _row(conn, operation_id)
        if not row or row["state"] != "committed":
            return
        record = dict(row)
    identity = load_identity(settings.db_path)
    if identity is None or credential_error(settings.credential_path):
        return
    body = {"operation_id": operation_id, "grant_id": record["grant_id"], "base_generation": record["base_generation"],
            "generation": record["generation"], "archive_sha256": record["archive_sha256"], "archive_size": record["archive_size"],
            "lock_sha256": json.loads(record["manifest"])["lock_sha256"]}
    try:
        async with httpx.AsyncClient(base_url=identity["platform_url"], headers=machine_headers(settings, identity), timeout=10) as client:
            response = await client.post(f"/v1/device/dev-sessions/{record['session_id']}/generations", json=body)
            if response.status_code == 204:
                (Path(settings.stage_dir) / record["staged_archive"]).unlink(missing_ok=True)
                with sqlite3.connect(settings.db_path) as conn:
                    conn.execute("UPDATE local_source_syncs SET state='reported' WHERE operation_id=? AND state='committed'", (operation_id,))
    except (httpx.HTTPError, OSError):
        return


def clean_orphaned_staging(settings, *, now=None):
    """Remove old files not referenced by a pending sync after interrupted writes."""
    root = Path(settings.stage_dir) / "dev-sync"
    if root.is_symlink() or not root.is_dir():
        return
    with sqlite3.connect(settings.db_path) as conn:
        referenced = {row[0] for row in conn.execute("SELECT staged_archive FROM local_source_syncs WHERE state='pending'")}
    cutoff = (now if now is not None else time.time()) - 300
    for directory in root.iterdir():
        try:
            if directory.is_symlink() or not directory.is_dir() or str(uuid.UUID(directory.name)) != directory.name:
                continue
            for path in directory.iterdir():
                if path.is_symlink() or not path.is_file() or path.stat().st_mtime > cutoff:
                    continue
                ref = f"dev-sync/{directory.name}/{path.name}"
                if ref in referenced:
                    continue
                if path.name.startswith(".source-") or path.suffix == ".zip" and str(uuid.UUID(path.stem)) == path.stem:
                    path.unlink(missing_ok=True)
        except (OSError, ValueError):
            continue


async def reconcile(settings):
    cleanup_cycle = 0
    while True:
        try:
            if cleanup_cycle % 30 == 0:
                clean_orphaned_staging(settings)
            cleanup_cycle += 1
            with sqlite3.connect(settings.db_path) as conn:
                rows = conn.execute("SELECT operation_id,state FROM local_source_syncs WHERE state IN ('pending','committed') ORDER BY session_id,generation LIMIT 40").fetchall()
            for operation_id, state in rows:
                if state == "pending":
                    await process(settings, operation_id)
                else:
                    await report(settings, operation_id)
        except (OSError, sqlite3.Error):
            logger.warning("Local source reconciliation encountered a storage error; retrying.")
        await asyncio.sleep(2)
