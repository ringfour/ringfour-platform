"""App-only LAN source snapshots. The regular App executor still owns runs."""

import asyncio
import base64
import hashlib
import hmac
import io
import json
import logging
import os
import re
import shutil
import stat
import uuid
import zipfile
from contextlib import closing
from datetime import UTC, datetime
from pathlib import Path, PurePosixPath

import httpx
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse

from .db import connect
from .enrollment import load_identity

logger = logging.getLogger(__name__)
MAX_ARCHIVE = 16 * 1024 * 1024
MAX_EXTRACTED = 32 * 1024 * 1024


def stamp(value):
    return datetime.fromisoformat(value)


def source_files(raw):
    """Verify a complete snapshot without extracting untrusted paths."""
    if len(raw) > MAX_ARCHIVE:
        raise ValueError("Source archive exceeds 16 MiB")
    files, total = {}, 0
    with zipfile.ZipFile(io.BytesIO(raw)) as archive:
        if not 1 <= len(archive.infolist()) <= 1024:
            raise ValueError("Source snapshot requires 1–1024 regular files")
        for entry in archive.infolist():
            path = PurePosixPath(entry.filename)
            mode = entry.external_attr >> 16
            if (
                entry.is_dir()
                or path.is_absolute()
                or str(path) != entry.filename
                or any(
                    p in (".", "..", "__pycache__") or p.startswith(".")
                    for p in path.parts
                )
                or not re.fullmatch(r"[A-Za-z0-9_][A-Za-z0-9_./-]*", entry.filename)
                or entry.filename in files
                or stat.S_IFMT(mode) not in (0, stat.S_IFREG)
                or entry.file_size > 8 * 1024 * 1024
                or entry.flag_bits & 1
            ):
                raise ValueError("Unsafe, duplicate or oversized source file")
            total += entry.file_size
            if total > MAX_EXTRACTED:
                raise ValueError("Source snapshot exceeds 32 MiB extracted")
            data = archive.read(entry)
            if len(data) != entry.file_size:
                raise ValueError("Incomplete source snapshot")
            files[entry.filename] = data
    return files


def source_path(settings, session, generation):
    identity = load_identity(settings.db_path)
    if not identity:
        raise ValueError("Robot is not enrolled")
    root = (
        Path(settings.db_path).parent
        / "app-development"
        / identity["deployment_namespace"]
        / str(uuid.UUID(session))
    )
    return root / f"{int(generation)}.zip"


def session_body(settings, session_id):
    identity = load_identity(settings.db_path)
    with closing(connect(settings.db_path)) as conn:
        row = conn.execute(
            "SELECT robot_id,namespace,body,closed FROM app_development_sessions WHERE session_id=?",
            (session_id,),
        ).fetchone()
    if (
        not identity
        or not row
        or row[3]
        or row[:2] != (identity["robot_id"], identity["deployment_namespace"])
    ):
        raise ValueError("Development session is closed")
    body = json.loads(row[2])
    if stamp(body["expires_at"]) <= datetime.now(UTC):
        raise ValueError("Development session expired")
    return body


class InvalidGrant(ValueError):
    pass


def authorize(settings, session_id, authorization):
    try:
        body = session_body(settings, session_id)
    except ValueError as error:
        raise InvalidGrant(str(error)) from error
    token = (
        authorization.removeprefix("Bearer ")
        if authorization.startswith("Bearer ")
        else ""
    )
    try:
        raw = base64.b64decode(
            token + "=" * (-len(token) % 4), altchars=b"-_", validate=True
        )
    except ValueError:
        raw = b""
    if (
        len(raw) != 32
        or not body.get("grant_hash")
        or not body.get("grant_expires_at")
        or stamp(body["grant_expires_at"]) <= datetime.now(UTC)
        or not hmac.compare_digest(hashlib.sha256(raw).hexdigest(), body["grant_hash"])
    ):
        raise InvalidGrant("A valid local development grant is required")
    return body


def commit(settings, session_id, operation_id, base, digest, raw):
    source_files(raw)
    session_body(settings, session_id)
    if digest != "sha256:" + hashlib.sha256(raw).hexdigest():
        raise ValueError("Source digest mismatch")
    with closing(connect(settings.db_path)) as conn, conn:
        conn.execute("BEGIN IMMEDIATE")
        previous = conn.execute(
            "SELECT base_generation,generation,archive_sha256 FROM app_development_source WHERE operation_id=? AND session_id=?",
            (operation_id, session_id),
        ).fetchone()
        if previous:
            if previous[0] != base or previous[2] != digest:
                raise ValueError("Source operation was reused with different input")
            return {"generation": previous[1], "archive_sha256": digest}
        current = conn.execute(
            "SELECT generation,archive_sha256,reported FROM app_development_source WHERE session_id=? ORDER BY generation DESC LIMIT 1",
            (session_id,),
        ).fetchone()
        if base != (current[0] if current else 0):
            raise ValueError("stale_generation")
        if current and not current[2]:
            raise ValueError("Previous source report is pending; retry shortly")
        if (
            shutil.disk_usage(Path(settings.db_path).parent).free
            < MAX_EXTRACTED + 256 * 1024 * 1024
        ):
            raise ValueError("Insufficient free storage for a source snapshot")
        destination = source_path(settings, session_id, base + 1)
        destination.parent.mkdir(parents=True, mode=0o700, exist_ok=True)
        temporary = destination.with_suffix(".pending")
        descriptor = os.open(
            temporary, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, 0o600
        )
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(raw)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, destination)
        conn.execute(
            "INSERT INTO app_development_source(operation_id,session_id,base_generation,generation,archive_sha256) VALUES (?,?,?,?,?)",
            (operation_id, session_id, base, base + 1, digest),
        )
        return {"generation": base + 1, "archive_sha256": digest}


def snapshot_files(settings, development):
    session_body(settings, development["session_id"])
    if stamp(development["expires_at"]) <= datetime.now(UTC):
        raise ValueError("Development snapshot expired")
    raw = source_path(
        settings, development["session_id"], development["generation"]
    ).read_bytes()
    if "sha256:" + hashlib.sha256(raw).hexdigest() != development["archive_sha256"]:
        raise ValueError("Development snapshot changed")
    return source_files(raw)


def create_local_app(settings, control_app):
    app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None)
    lock = asyncio.Lock()

    @app.exception_handler(zipfile.BadZipFile)
    @app.exception_handler(ValueError)
    async def invalid(_request, error):
        return JSONResponse(
            {"error": str(error)},
            status_code=401 if isinstance(error, InvalidGrant) else 409,
        )

    @app.get("/v1/local/app-development/{session_id}/source")
    async def state(session_id: uuid.UUID, request: Request):
        authorize(settings, str(session_id), request.headers.get("authorization", ""))
        with closing(connect(settings.db_path)) as conn:
            row = conn.execute(
                "SELECT generation,archive_sha256,reported FROM app_development_source WHERE session_id=? ORDER BY generation DESC LIMIT 1",
                (str(session_id),),
            ).fetchone()
        return {
            "generation": row[0] if row else 0,
            "archive_sha256": row[1] if row else None,
            "report_pending": bool(row and not row[2]),
        }

    @app.put("/v1/local/app-development/{session_id}/source")
    async def sync(session_id: uuid.UUID, request: Request):
        authorize(settings, str(session_id), request.headers.get("authorization", ""))
        operation = str(uuid.UUID(request.headers.get("idempotency-key", "")))
        base = int(request.headers.get("x-base-generation", "-1"))
        if not 0 <= base < 2147483647:
            raise ValueError("Invalid base generation")
        raw = bytearray()
        async for chunk in request.stream():
            raw.extend(chunk)
            if len(raw) > MAX_ARCHIVE:
                raise ValueError("Source archive exceeds 16 MiB")
        async with lock:
            authorize(
                settings, str(session_id), request.headers.get("authorization", "")
            )
            return await asyncio.to_thread(
                commit,
                settings,
                str(session_id),
                operation,
                base,
                request.headers.get("x-archive-sha256"),
                bytes(raw),
            )

    return app


def poll_once(settings, client, fingerprint):
    identity = load_identity(settings.db_path)
    if not identity:
        return
    headers = {
        "x-ringfour-credential-id": identity["credential_id"],
        "x-ringfour-credential-secret": Path(settings.credential_path)
        .read_text()
        .strip(),
    }
    base = identity["platform_url"].rstrip("/")
    response = client.put(
        base + "/v1/device/app-development/endpoint",
        headers=headers,
        json={
            "daemon_url": settings.development_url,
            "certificate_sha256": fingerprint,
        },
    )
    if response.status_code == 401:
        with closing(connect(settings.db_path)) as conn, conn:
            conn.execute("UPDATE app_development_sessions SET closed=1")
        return
    if response.status_code == 401:
        with closing(connect(settings.db_path)) as conn, conn:
            conn.execute("UPDATE app_development_sessions SET closed=1")
        return
    response.raise_for_status()
    response = client.get(base + "/v1/device/app-development", headers=headers)
    if response.status_code == 401:
        with closing(connect(settings.db_path)) as conn, conn:
            conn.execute("UPDATE app_development_sessions SET closed=1")
        return
    response.raise_for_status()
    body = response.json()["session"]
    with closing(connect(settings.db_path)) as conn, conn:
        conn.execute("UPDATE app_development_sessions SET closed=1")
        if body:
            conn.execute(
                "INSERT INTO app_development_sessions(session_id,robot_id,namespace,body,closed) VALUES (?,?,?,?,0) ON CONFLICT(session_id) DO UPDATE SET body=excluded.body,closed=0",
                (
                    body["session_id"],
                    identity["robot_id"],
                    identity["deployment_namespace"],
                    json.dumps(body),
                ),
            )
        reports = conn.execute(
            "SELECT operation_id,session_id,base_generation,generation,archive_sha256 FROM app_development_source WHERE reported=0"
        ).fetchall()
    for operation, session, base_generation, generation, digest in reports:
        if not body or session != body["session_id"]:
            continue
        response = client.post(
            base + f"/v1/device/app-development/{session}/source",
            headers=headers,
            json={
                "operation_id": operation,
                "base_generation": base_generation,
                "generation": generation,
                "archive_sha256": digest,
            },
        )
        response.raise_for_status()
        with closing(connect(settings.db_path)) as conn, conn:
            conn.execute(
                "UPDATE app_development_source SET reported=1 WHERE operation_id=?",
                (operation,),
            )


def enforce_sessions(settings, manager):
    executor = manager.app_executor
    if not executor:
        return
    stopped = []
    with executor.mutex, closing(connect(settings.db_path)) as conn:
        for run_id, snapshot in conn.execute(
            "SELECT run_id,snapshot_json FROM app_runs WHERE state IN ('accepted','starting','running')"
        ).fetchall():
            development = json.loads(snapshot).get("development")
            if development:
                try:
                    session_body(settings, development["session_id"])
                except ValueError:
                    stopped.append(run_id)
    for run_id in stopped:
        with executor.mutex:
            row, intent = executor.row(run_id)
            if row["state"] not in ("accepted", "starting", "running"):
                continue
            if row["started_at"] is None and (
                intent is None or intent["phase"] in ("create", "created")
            ):
                executor.interrupt(run_id)
                continue
        executor.stop(run_id)


async def run(settings, manager):
    fingerprint = None
    if settings.development_url:
        from .development_tls import local_identity

        try:
            fingerprint = await asyncio.to_thread(
                local_identity, settings.development_tls_path
            )
        except (OSError, ValueError):
            logger.error(
                "Development TLS identity is unavailable; closing cached sessions"
            )
    if fingerprint is None:
        with closing(connect(settings.db_path)) as conn, conn:
            conn.execute("UPDATE app_development_sessions SET closed=1")
    while True:
        if fingerprint is not None:
            try:
                with httpx.Client(timeout=5, trust_env=False) as client:
                    await asyncio.to_thread(poll_once, settings, client, fingerprint)
            except (OSError, ValueError, httpx.HTTPError):
                logger.warning(
                    "Development coordination unavailable; retaining bounded local session state"
                )
        try:
            await asyncio.to_thread(enforce_sessions, settings, manager)
        except (OSError, ValueError):
            logger.warning("Development run cleanup is pending")
        await asyncio.sleep(2)


def local_app():
    """TLS listener factory for infrastructure that runs separate ASGI processes."""
    from .config import Settings
    from .db import migrate

    settings = Settings.from_env()
    migrate(settings.db_path)
    return create_local_app(settings, None)
