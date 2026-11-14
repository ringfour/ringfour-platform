"""Execution-scoped private endpoint for mission-authored entries."""
import hashlib
import hmac
import json
import math
import re
import sqlite3
import time
import uuid

import httpx
from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse

from . import logbook
from .enrollment import load_identity

router = APIRouter()
_FIELD = re.compile(r"[a-zA-Z][a-zA-Z0-9_]{0,63}\Z")
_MAC = re.compile(r"[0-9a-f]{64}\Z")


def error(status, code, message):
    return JSONResponse({"error": {"code": code, "message": message, "request_id": str(uuid.uuid4())}}, status_code=status)


def _unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate JSON key")
        result[key] = value
    return result


def validate(raw):
    value = json.loads(raw, object_pairs_hook=_unique, parse_constant=lambda _: (_ for _ in ()).throw(ValueError()))
    if not isinstance(value, dict) or set(value) != {"entry_id", "level", "message", "fields"} or not logbook._id(value["entry_id"]) or value["level"] != "info":
        raise ValueError("Invalid entry")
    message, fields = value["message"], value["fields"]
    if not isinstance(message, str) or not message.strip() or "\x00" in message or len(message) > 500 or not isinstance(fields, dict) or len(fields) > 16:
        raise ValueError("Invalid entry")
    for key, item in fields.items():
        if not _FIELD.fullmatch(key):
            raise ValueError("Invalid field")
        if item is None or type(item) is bool:
            continue
        if type(item) is int and -(2 ** 53 - 1) <= item <= 2 ** 53 - 1:
            continue
        if isinstance(item, str) and len(item) <= 512 and "\x00" not in item:
            continue
        if type(item) is float and math.isfinite(item) and abs(item) <= 2 ** 53 - 1:
            continue
        raise ValueError("Invalid field")
    json.dumps(value, ensure_ascii=False, allow_nan=False).encode("utf-8")
    return value


def _execution_token(settings, header):
    if not settings.runner_auth_token or not header.startswith("Bearer "):
        return None
    token = header.removeprefix("Bearer ")
    execution, separator, mac = token.partition(":")
    if not separator or not logbook._id(execution) or not _MAC.fullmatch(mac):
        return None
    expected = hmac.new(settings.runner_auth_token.encode(), ("ringfour-logbook-v1:" + execution).encode(), hashlib.sha256).hexdigest()
    return execution if hmac.compare_digest(mac, expected) else None


def _operation(db_path, robot_id, execution):
    with sqlite3.connect(db_path) as conn:
        row = conn.execute("SELECT operation_id FROM local_dev_runs WHERE execution_id=?", (execution,)).fetchone()
        operation = row[0] if row else execution
        row = conn.execute("SELECT 1 FROM device_operations WHERE operation_id=? AND robot_id=? AND kind IN ('dev.session.restart','script.execution.start')", (operation, robot_id)).fetchone()
        return operation if row else None


def _record(settings, robot, execution, operation, entry):
    canonical = json.dumps({"execution_id": execution, **entry}, sort_keys=True, ensure_ascii=False, separators=(",", ":"), allow_nan=False)
    fingerprint = hashlib.sha256(canonical.encode()).hexdigest()
    with sqlite3.connect(settings.db_path, timeout=5) as conn:
        conn.execute("BEGIN IMMEDIATE")
        replay = conn.execute("SELECT execution_id,request_hash FROM logbook_entry_replays WHERE entry_id=?", (entry["entry_id"],)).fetchone()
        if replay:
            if replay != (execution, fingerprint):
                raise logbook.OutboxError("conflict")
            return
        checkpoint = conn.execute("SELECT acknowledged_sequence FROM logbook_outbox_state WHERE singleton_id=1").fetchone()
        conn.execute("DELETE FROM logbook_entry_replays WHERE created_at < ? AND local_sequence <= ?", (time.time() - 86400, checkpoint[0] if checkpoint else 0))
        if conn.execute("SELECT count(*) FROM logbook_entry_replays").fetchone()[0] >= 8192:
            raise logbook.OutboxFull("Replay capacity exhausted")
        try:
            event = logbook.append(conn, robot, operation, None, event_id=entry["entry_id"], execution_id=execution,
                author_payload={"level": entry["level"], "message": entry["message"], "fields": entry["fields"]})
        except sqlite3.IntegrityError as exc:
            raise logbook.OutboxError("conflict") from exc
        conn.execute("INSERT INTO logbook_entry_replays VALUES (?,?,?,?,?)", (entry["entry_id"], execution, fingerprint, event["local_sequence"], time.time()))


@router.post("/v1/runtime/logbook/entries")
async def create_entry(request: Request):
    settings = request.app.state.settings
    execution = _execution_token(settings, request.headers.get("authorization", ""))
    if not execution:
        return error(401, "invalid_token", "A valid execution credential is required.")
    try:
        data = bytearray()
        async for chunk in request.stream():
            data.extend(chunk)
            if len(data) > 8192:
                return error(413, "size_limit", "Logbook entry exceeds its limit.")
        entry = validate(data)
    except (ValueError, UnicodeError, TypeError, RecursionError):
        return error(400, "invalid_input", "Invalid Logbook entry.")
    try:
        identity = load_identity(settings.db_path)
        if identity is None:
            return error(401, "invalid_token", "A valid execution credential is required.")
        operation = _operation(settings.db_path, identity["robot_id"], execution)
        if not operation:
            return error(401, "invalid_token", "A valid execution credential is required.")
        async with httpx.AsyncClient(base_url=settings.runner_url, timeout=5, headers={"authorization": f"Bearer {settings.runner_auth_token}"}) as client:
            response = await client.get(f"/v1/executions/{execution}")
            if response.status_code == 404:
                return error(401, "invalid_token", "A valid execution credential is required.")
            response.raise_for_status()
            run = response.json()
        if not isinstance(run, dict) or run.get("execution_id") != execution or run.get("state") != "running":
            return error(401, "invalid_token", "A valid execution credential is required.")
        _record(settings, identity["robot_id"], execution, operation, entry)
    except logbook.OutboxFull:
        return error(503, "capacity_exceeded", "Logbook storage is full.")
    except logbook.OutboxError:
        return error(409, "conflict", "Logbook entry conflicts with an existing record.")
    except (httpx.HTTPError, sqlite3.Error, OSError, ValueError):
        return error(503, "runtime_unavailable", "Logbook runtime is unavailable.")
    return {"entry_id": entry["entry_id"]}
