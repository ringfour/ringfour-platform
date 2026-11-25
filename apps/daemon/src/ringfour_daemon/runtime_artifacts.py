"""Durable execution-scoped live artifact publication and delivery."""
import asyncio
import json
import logging
import re
import sqlite3
import uuid

import httpx
from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse

from .app_engine import EngineError
from .enrollment import load_identity
from .runtime_logbook import _execution_token, _operation

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
            manager = getattr(request.app.state, "connection_manager", None)
            executor = getattr(manager, "app_executor", None) or getattr(request.app.state, "app_executor", None)
            if executor is None:
                return error(503, "runtime_unavailable", "App executor is unavailable.")
            return await asyncio.to_thread(snapshot, settings, executor, execution, value)
        return error(401, "invalid_token", "A valid App run credential is required.")
    except EngineError:
        return error(503, "runtime_unavailable", "App engine is unavailable.")
    except FileExistsError:
        return error(409, "conflict", "Artifact ID conflicts with an existing publication.")
    except (ValueError, TypeError, KeyError, json.JSONDecodeError):
        return error(400, "invalid_input", "Invalid artifact publication.")
    except (httpx.HTTPError, sqlite3.Error, OSError):
        return error(503, "runtime_unavailable", "Artifact runtime is unavailable.")
