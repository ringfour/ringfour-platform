"""Dedicated local HTTPS application; no public enrollment or status routes."""
import json
import sqlite3
import uuid

import httpx
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse

from . import local_execution, local_source
from .local_grants import authorize, grant_identity


def create_local_app(settings):
    app = FastAPI(title="Ringfour local development", docs_url=None, redoc_url=None, openapi_url=None)

    @app.get("/v1/local/dev-sessions/{session_id}")
    async def session_status(session_id: str, request: Request):
        header = request.headers.get("authorization", "")
        token = header[7:] if header.startswith("Bearer ") else ""
        if not authorize(settings.db_path, session_id, token):
            return JSONResponse({"error": {"code": "invalid_grant", "message": "A valid local grant is required.", "request_id": str(uuid.uuid4())}}, status_code=401)
        with sqlite3.connect(settings.db_path) as conn:
            generation = conn.execute("SELECT coalesce(max(generation), 0) FROM local_source_syncs WHERE session_id=? AND state IN ('committed','reported')", (session_id,)).fetchone()[0]
            pending = conn.execute("SELECT count(*) FROM local_source_syncs WHERE session_id=? AND state='committed'", (session_id,)).fetchone()[0]
        return {"session_id": session_id, "state": "ready", "generation": generation, "report_pending": pending}

    async def execution_response(session_id, execution_id, request, *, output=False, stream=None, offset=None, limit=None):
        header = request.headers.get("authorization", "")
        token = header[7:] if header.startswith("Bearer ") else ""
        if not authorize(settings.db_path, session_id, token):
            return JSONResponse({"error": {"code": "invalid_grant", "message": "A valid local grant is required.", "request_id": str(uuid.uuid4())}}, status_code=401)
        if not local_execution.belongs(settings.db_path, session_id, execution_id):
            return JSONResponse({"error": {"code": "not_found", "message": "Execution not found.", "request_id": str(uuid.uuid4())}}, status_code=404)
        if not settings.runner_auth_token:
            return JSONResponse({"error": {"code": "runner_unavailable", "message": "Runner is unavailable.", "request_id": str(uuid.uuid4())}}, status_code=503)
        if output and (stream not in ("stdout", "stderr") or offset is None or limit is None or not 0 <= offset <= 65536 or not 1 <= limit <= 16384):
            return JSONResponse({"error": {"code": "invalid_input", "message": "Invalid output range.", "request_id": str(uuid.uuid4())}}, status_code=400)
        try:
            async with httpx.AsyncClient(base_url=settings.runner_url, headers={"authorization": f"Bearer {settings.runner_auth_token}"}, timeout=10) as client:
                path = f"/v1/executions/{execution_id}" + ("/output" if output else "")
                response = await client.get(path, params={"stream": stream, "offset": offset, "limit": limit} if output else None)
                if response.status_code == 404:
                    return JSONResponse({"error": {"code": "not_found", "message": "Execution not found.", "request_id": str(uuid.uuid4())}}, status_code=404)
                response.raise_for_status()
                return response.json()
        except (httpx.HTTPError, ValueError):
            return JSONResponse({"error": {"code": "runner_unavailable", "message": "Runner is unavailable.", "request_id": str(uuid.uuid4())}}, status_code=503)

    @app.get("/v1/local/dev-sessions/{session_id}/executions/{execution_id}")
    async def execution(session_id: str, execution_id: str, request: Request):
        return await execution_response(session_id, execution_id, request)

    @app.get("/v1/local/dev-sessions/{session_id}/executions/{execution_id}/output")
    async def execution_output(session_id: str, execution_id: str, request: Request, stream: str = "", offset: int = -1, limit: int = -1):
        return await execution_response(session_id, execution_id, request, output=True, stream=stream, offset=offset, limit=limit)

    @app.get("/v1/local/dev-sessions/{session_id}/source")
    async def source_state(session_id: str, request: Request):
        header = request.headers.get("authorization", "")
        token = header[7:] if header.startswith("Bearer ") else ""
        if not authorize(settings.db_path, session_id, token):
            return JSONResponse({"error": {"code": "invalid_grant", "message": "A valid local grant is required.", "request_id": str(uuid.uuid4())}}, status_code=401)
        with sqlite3.connect(settings.db_path) as conn:
            row = conn.execute("SELECT generation,manifest,archive_sha256 FROM local_source_syncs WHERE session_id=? AND state IN ('committed','reported') ORDER BY generation DESC LIMIT 1", (session_id,)).fetchone()
        return {"generation": row[0], "paths": sorted(item["path"] for item in json.loads(row[1])["files"]), "archive_sha256": row[2]} if row else {"generation": 0, "paths": [], "archive_sha256": None}

    @app.put("/v1/local/dev-sessions/{session_id}/source")
    async def sync_source(session_id: str, request: Request):
        header = request.headers.get("authorization", "")
        token = header[7:] if header.startswith("Bearer ") else ""
        grant_id = grant_identity(settings.db_path, session_id, token)
        if not grant_id:
            return JSONResponse({"error": {"code": "invalid_grant", "message": "A valid local grant is required.", "request_id": str(uuid.uuid4())}}, status_code=401)
        try:
            raw = bytearray()
            async for chunk in request.stream():
                raw.extend(chunk)
                if len(raw) > local_source.MAX_BODY:
                    return JSONResponse({"error": {"code": "source_too_large", "message": "Source request exceeds its limit.", "request_id": str(uuid.uuid4())}}, status_code=413)
            body = json.loads(raw)
            if grant_identity(settings.db_path, session_id, token) != grant_id:
                return JSONResponse({"error": {"code": "invalid_grant", "message": "A valid local grant is required.", "request_id": str(uuid.uuid4())}}, status_code=401)
            operation = local_source.prepare(settings, session_id, grant_id, request.headers.get("idempotency-key", ""), body)
            result = await local_source.process(settings, operation)
            if result is None:
                return JSONResponse({"error": {"code": "sync_pending", "message": "Source sync is pending; retry with the same key.", "request_id": str(uuid.uuid4())}}, status_code=503)
            if "error" in result:
                code = result["error"]
                return JSONResponse({"error": {"code": code, "message": "The source sync was not committed.", "request_id": str(uuid.uuid4())}}, status_code=400 if code == "invalid_source" else 409)
            return result
        except (ValueError, TypeError, json.JSONDecodeError) as exc:
            code = str(exc) if str(exc) in {"invalid_key", "invalid_source", "session_unavailable", "conflict", "sync_in_progress", "stale_generation", "capacity_exceeded", "source_too_large"} else "invalid_source"
            status = 413 if code == "source_too_large" else 400 if code in {"invalid_key", "invalid_source"} else 409
            return JSONResponse({"error": {"code": code, "message": "The source sync was not committed.", "request_id": str(uuid.uuid4())}}, status_code=status)
        except (OSError, sqlite3.Error):
            return JSONResponse({"error": {"code": "storage_unavailable", "message": "Local storage is unavailable.", "request_id": str(uuid.uuid4())}}, status_code=503)

    return app
