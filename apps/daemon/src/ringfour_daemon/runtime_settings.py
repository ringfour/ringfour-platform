"""Run-scoped App settings polling with durable, idempotent result capture."""

import asyncio
import json
import sqlite3
import uuid
from contextlib import closing

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse

from . import app_runtime, app_validation
from .agent_inventory import utc_now
from .db import connect
from .enrollment import load_identity

router = APIRouter()
MAX_BODY = 16_384


def accept_command(db_path, robot_id, command):
    payload = command["payload"]
    identity = load_identity(db_path)
    if (
        identity is None
        or identity["robot_id"] != robot_id
        or identity["deployment_namespace"] != payload["deployment_namespace"]
        or command["operation_id"] != payload["application_id"]
    ):
        raise ValueError("settings command identity mismatch")
    run = payload["run_id"]
    with closing(connect(db_path, timeout=5)) as conn, conn:
        conn.execute("BEGIN IMMEDIATE")
        # An authenticated settings command can precede its start command.
        # Persist it without blocking the device stream; pending/result routes
        # require the exact active run credential and verified container state.
        values = app_validation.canonical(payload["values"])
        existing = conn.execute(
            "SELECT run_id,installation_id,settings_revision_id,values_json,initial FROM app_settings_applications WHERE application_id=?",
            (payload["application_id"],),
        ).fetchone()
        expected = (run, payload["installation_id"], payload["settings_revision_id"], values, int(payload["initial"]))
        if existing:
            if tuple(existing) != expected:
                raise ValueError("conflicting settings application identity")
            return
        conn.execute(
            "INSERT INTO app_settings_applications(application_id,run_id,installation_id,settings_revision_id,values_json,initial) VALUES (?,?,?,?,?,?)",
            (payload["application_id"], *expected),
        )


def _error(status, code, message):
    return JSONResponse({"error": {"code": code, "message": message}}, status_code=status)


def _run(request):
    return app_runtime.authenticate(request.app.state.settings, request.headers.get("authorization", ""))


@router.get("/v1/runtime/settings/pending")
async def pending(request: Request):
    run_id = _run(request)
    if run_id is None:
        return _error(401, "unauthenticated", "A valid active App run credential is required.")
    if await asyncio.to_thread(app_runtime.running, request.app.state.settings, run_id) is not True:
        return _error(503, "engine_unavailable", "The App container state cannot be verified.")
    with closing(connect(request.app.state.settings.db_path)) as conn:
        conn.row_factory = sqlite3.Row
        rows = conn.execute(
            "SELECT application_id,settings_revision_id,values_json,initial FROM app_settings_applications WHERE run_id=? AND outcome IS NULL ORDER BY rowid LIMIT 2",
            (run_id,),
        ).fetchall()
    if len(rows) > 1:
        return _error(409, "invalid_state", "More than one settings application is pending for this run.")
    value = None if not rows else {
        "application_id": rows[0]["application_id"],
        "settings_revision_id": rows[0]["settings_revision_id"],
        "values": json.loads(rows[0]["values_json"]),
        "initial": bool(rows[0]["initial"]),
    }
    result = {"pending": value}
    if len(json.dumps(result, separators=(",", ":")).encode()) > MAX_BODY:
        return _error(500, "invalid_state", "Settings response exceeded its size limit.")
    return result


@router.post("/v1/runtime/settings/{application_id}/result")
async def result(application_id: str, request: Request):
    run_id = _run(request)
    if run_id is None:
        return _error(401, "unauthenticated", "A valid active App run credential is required.")
    if await asyncio.to_thread(app_runtime.running, request.app.state.settings, run_id) is not True:
        return _error(503, "engine_unavailable", "The App container state cannot be verified.")
    try:
        app_id = str(uuid.UUID(application_id))
        raw = await request.body()
        if len(raw) > MAX_BODY:
            raise ValueError()
        value = json.loads(raw, object_pairs_hook=lambda pairs: _unique(pairs))
        if not isinstance(value, dict) or set(value) - {"outcome", "reason"} or value.get("outcome") not in ("applied", "rejected"):
            raise ValueError()
        reason = value.get("reason")
        if reason is not None and (not isinstance(reason, str) or len(reason) > 240 or any(c in reason for c in "\x00\r\n")):
            raise ValueError()
        if value["outcome"] == "applied" and reason is not None:
            raise ValueError()
        app_validation.canonical({"outcome": value["outcome"], "reason": reason})
    except (ValueError, TypeError, KeyError, json.JSONDecodeError):
        return _error(400, "invalid_input", "The settings result is invalid.")
    with closing(connect(request.app.state.settings.db_path, timeout=5)) as conn, conn:
        conn.execute("BEGIN IMMEDIATE")
        row = conn.execute(
            "SELECT outcome,reason FROM app_settings_applications WHERE application_id=? AND run_id=?",
            (app_id, run_id),
        ).fetchone()
        if row is None:
            return _error(404, "not_found", "Settings application is not accessible for this run.")
        if row[0] is not None:
            if row[0] != value["outcome"] or row[1] != reason:
                return _error(409, "settings_result_conflict", "A different result was already recorded.")
            return {"accepted": True, "application_id": app_id}
        conn.execute(
            "UPDATE app_settings_applications SET outcome=?,reason=?,result_recorded_at=? WHERE application_id=? AND run_id=? AND outcome IS NULL",
            (value["outcome"], reason, utc_now(), app_id, run_id),
        )
    manager = getattr(request.app.state, "connection_manager", None)
    if manager is not None:
        manager.app_work.set()
    return {"accepted": True, "application_id": app_id}


def _unique(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError("duplicate JSON key")
        value[key] = item
    return value
