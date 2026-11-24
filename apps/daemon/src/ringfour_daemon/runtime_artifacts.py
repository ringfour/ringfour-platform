"""Durable execution-scoped live artifact publication and delivery."""
import asyncio
import hashlib
import json
import logging
import re
import sqlite3
import tempfile
import time
import uuid

import httpx
from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse

from .app_engine import EngineError
from .db import connect
from .enrollment import credential_error, load_identity
from .runtime_logbook import _execution_token, _operation
from .scripts import machine_headers

router = APIRouter()
logger = logging.getLogger(__name__)
_PATH = re.compile(r"(?!/)(?!.*(?:^|/)\.\.?(?:/|$))(?!.*\\)[^\x00-\x1f\x7f]{1,240}\Z")
_TYPES = {"image/jpeg", "image/png", "application/octet-stream"}


def error(status, code, message):
    return JSONResponse({"error": {"code": code, "message": message, "request_id": str(uuid.uuid4())}}, status_code=status)


def validate(raw):
    value = json.loads(raw)
    if not isinstance(value, dict) or set(value) != {"artifact_id", "path", "content_type"}:
        raise ValueError
    if str(uuid.UUID(value["artifact_id"])) != value["artifact_id"] or not _PATH.fullmatch(value["path"]) or value["content_type"] not in _TYPES:
        raise ValueError
    return value


def enqueue(db_path, execution, artifact):
    values = (artifact["artifact_id"], execution, artifact["path"], artifact["size"], artifact["sha256"], artifact["content_type"])
    with connect(db_path, timeout=5) as conn:
        row = conn.execute("SELECT artifact_id,execution_id,path,size,sha256,content_type FROM live_artifact_queue WHERE artifact_id=?",
                           (artifact["artifact_id"],)).fetchone()
        if row and row != values:
            raise FileExistsError
        if not row:
            conn.execute("INSERT INTO live_artifact_queue (artifact_id,execution_id,path,size,sha256,content_type,created_at) VALUES (?,?,?,?,?,?,?)", (*values, time.time()))


@router.post("/v1/runtime/artifacts")
async def publish(request: Request):
    settings = request.app.state.settings
    execution = _execution_token(settings, request.headers.get("authorization", ""))
    if not execution:
        return error(401, "invalid_token", "A valid execution credential is required.")
    try:
        raw = await request.body()
        if len(raw) > 4096:
            return error(413, "size_limit", "Artifact request exceeds its limit.")
        value = validate(raw)
        identity = load_identity(settings.db_path)
        if identity is None or not _operation(settings.db_path, identity["robot_id"], execution):
            return error(401, "invalid_token", "A valid execution credential is required.")
        from .app_runtime import local_run, running, snapshot
        if local_run(settings, execution) is not None:
            if await asyncio.to_thread(running, settings, execution) is not True:
                return error(503, "runtime_unavailable", "App engine state is unavailable.")
            executor = getattr(request.app.state, "app_executor", None)
            if executor is None:
                return error(503, "runtime_unavailable", "App executor is unavailable.")
            return await asyncio.to_thread(snapshot, settings, executor, execution, value)
        headers = {"authorization": f"Bearer {settings.runner_auth_token}"}
        async with httpx.AsyncClient(base_url=settings.runner_url, headers=headers, timeout=30) as client:
            response = await client.post(f"/v1/executions/{execution}/live-artifacts", json=value)
        if response.status_code in (400, 409):
            return error(response.status_code, "conflict" if response.status_code == 409 else "invalid_input", "Runner rejected the artifact publication.")
        response.raise_for_status()
        artifact = response.json()
        enqueue(settings.db_path, execution, artifact)
        return artifact
    except EngineError:
        return error(503, "runtime_unavailable", "App engine is unavailable.")
    except FileExistsError:
        return error(409, "conflict", "Artifact ID conflicts with an existing publication.")
    except (ValueError, TypeError, KeyError, json.JSONDecodeError):
        return error(400, "invalid_input", "Invalid artifact publication.")
    except (httpx.HTTPError, sqlite3.Error, OSError):
        return error(503, "runtime_unavailable", "Artifact runtime is unavailable.")


async def deliver_one(settings, identity, row):
    artifact_id, execution, path, size, sha256, content_type, state = row
    runner_headers = {"authorization": f"Bearer {settings.runner_auth_token}"}
    transfer_timeout = httpx.Timeout(None, connect=15, write=300)
    async with httpx.AsyncClient(base_url=settings.runner_url, headers=runner_headers, timeout=transfer_timeout) as local, \
            httpx.AsyncClient(base_url=identity["platform_url"], headers=machine_headers(settings, identity), timeout=transfer_timeout) as platform:
        if state == "pending":
            with tempfile.TemporaryFile() as data:
                digest = hashlib.sha256()
                length = 0
                async with local.stream("GET", f"/v1/executions/{execution}/live-artifacts/{artifact_id}/download") as response:
                    response.raise_for_status()
                    async for chunk in response.aiter_bytes(65536):
                        length += len(chunk)
                        if length > size:
                            raise ValueError("Artifact exceeds declared size")
                        digest.update(chunk)
                        await asyncio.to_thread(data.write, chunk)
                if length != size or digest.hexdigest() != sha256:
                    raise ValueError("Artifact digest mismatch")
                data.seek(0)

                async def chunks():
                    while chunk := await asyncio.to_thread(data.read, 65536):
                        yield chunk

                response = await platform.put(f"/v1/device/executions/{execution}/artifacts",
                    params={"artifact_id": artifact_id, "path": path, "sha256": sha256, "content_type": content_type},
                    headers={"Content-Length": str(size)}, content=chunks())
                response.raise_for_status()
            with connect(settings.db_path, timeout=5) as conn:
                conn.execute("UPDATE live_artifact_queue SET state='uploaded' WHERE artifact_id=?", (artifact_id,))
        response = await local.post(f"/v1/executions/{execution}/live-artifacts/{artifact_id}/ack")
        response.raise_for_status()
    with connect(settings.db_path, timeout=5) as conn:
        conn.execute("DELETE FROM live_artifact_queue WHERE artifact_id=?", (artifact_id,))


async def deliver(settings):
    while True:
        try:
            identity = load_identity(settings.db_path)
            if identity is not None and not credential_error(settings.credential_path) and settings.runner_auth_token:
                with connect(settings.db_path) as conn:
                    rows = conn.execute("SELECT artifact_id,execution_id,path,size,sha256,content_type,state FROM live_artifact_queue ORDER BY created_at LIMIT 2").fetchall()
                for row in rows:
                    try:
                        await deliver_one(settings, identity, row)
                    except (httpx.HTTPError, sqlite3.Error, OSError, ValueError):
                        logger.warning("live artifact delivery failed for %s; will retry", row[0])
        except (sqlite3.Error, OSError):
            logger.warning("live artifact queue unavailable; will retry")
        await asyncio.sleep(2)
