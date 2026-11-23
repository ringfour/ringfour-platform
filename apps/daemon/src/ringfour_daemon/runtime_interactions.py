"""Execution-scoped local human-interaction protocol.

The daemon is the authority for deadline and terminal-state decisions. Hosted
replication and response delivery build on this durable local state.
"""
import asyncio
import hashlib
import json
import logging
import re
import sqlite3
import time
import uuid
from datetime import UTC, datetime

import httpx
from fastapi import APIRouter, Request

from .db import connect
from .enrollment import credential_error, load_identity
from .runtime_logbook import _execution_token, _operation, error
from .scripts import machine_headers

router = APIRouter()
logger = logging.getLogger(__name__)
_VALUE = re.compile(r"[a-zA-Z][a-zA-Z0-9_.-]{0,63}\Z")


def _unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate JSON key")
        result[key] = value
    return result


def _timestamp(value: float) -> str:
    return datetime.fromtimestamp(value, UTC).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def _deadline(value: object) -> float | None:
    if value is None:
        return None
    if not isinstance(value, str) or not value.endswith("Z"):
        raise ValueError("Invalid deadline")
    parsed = datetime.fromisoformat(value)
    result = parsed.timestamp()
    now = time.time()
    if result <= now or result > now + 86400:
        raise ValueError("Deadline must be within the next 24 hours")
    return result


def _validate(raw: bytes) -> tuple[dict, float, str]:
    value = json.loads(raw, object_pairs_hook=_unique,
                       parse_constant=lambda _: (_ for _ in ()).throw(ValueError()))
    if not isinstance(value, dict) or set(value) != {"request_id", "prompt", "choices", "deadline", "timeout_outcome"}:
        raise ValueError("Invalid request")
    request_id, prompt, choices = value["request_id"], value["prompt"], value["choices"]
    if str(uuid.UUID(request_id)) != request_id:
        raise ValueError("Invalid request ID")
    if not isinstance(prompt, str) or not prompt.strip() or "\x00" in prompt or len(prompt) > 1000:
        raise ValueError("Invalid prompt")
    if not isinstance(choices, list) or not 2 <= len(choices) <= 12:
        raise ValueError("Invalid choices")
    seen = set()
    for choice in choices:
        if (not isinstance(choice, dict) or set(choice) != {"value", "label"}
                or not isinstance(choice["value"], str) or not _VALUE.fullmatch(choice["value"])
                or choice["value"] in seen or not isinstance(choice["label"], str)
                or not choice["label"].strip() or "\x00" in choice["label"] or len(choice["label"]) > 160):
            raise ValueError("Invalid choices")
        seen.add(choice["value"])
    deadline = _deadline(value["deadline"])
    timeout_outcome = value["timeout_outcome"]
    if (deadline is None) != (timeout_outcome is None):
        raise ValueError("Deadline and timeout outcome must be provided together")
    if timeout_outcome is not None and timeout_outcome not in seen:
        raise ValueError("Timeout outcome must name a choice")
    canonical = json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return value, deadline, hashlib.sha256(canonical.encode()).hexdigest()


def _document(row: sqlite3.Row) -> dict:
    return {
        "request_id": row["request_id"], "robot_id": row["robot_id"],
        "execution_id": row["execution_id"], "prompt": row["prompt"],
        "choices": json.loads(row["choices_json"]),
        "deadline": _timestamp(row["deadline"]) if row["deadline"] is not None else None,
        "timeout_outcome": row["timeout_outcome"], "state": row["state"],
        "outcome": row["outcome"],
        "response": json.loads(row["response_json"]) if row["response_json"] else None,
        "created_at": _timestamp(row["created_at"]), "updated_at": _timestamp(row["updated_at"]),
    }


def _load(db_path: str, request_id: str, execution: str, *, resolve_deadline: bool = True) -> dict | None:
    with connect(db_path, timeout=5) as conn:
        conn.row_factory = sqlite3.Row
        conn.execute("BEGIN IMMEDIATE")
        row = conn.execute("SELECT * FROM runtime_interactions WHERE request_id=? AND execution_id=?", (request_id, execution)).fetchone()
        if (row and resolve_deadline and row["state"] == "pending"
                and row["deadline"] is not None and row["deadline"] <= time.time()):
            now = time.time()
            conn.execute("UPDATE runtime_interactions SET state='timed_out',outcome=timeout_outcome,updated_at=? WHERE request_id=? AND state='pending'", (now, request_id))
            row = conn.execute("SELECT * FROM runtime_interactions WHERE request_id=?", (request_id,)).fetchone()
        return _document(row) if row else None


async def _context(request: Request) -> tuple[str, str] | None:
    settings = request.app.state.settings
    execution = _execution_token(settings, request.headers.get("authorization", ""))
    identity = load_identity(settings.db_path) if execution else None
    if not execution or identity is None or not _operation(settings.db_path, identity["robot_id"], execution):
        return None
    return execution, identity["robot_id"]


async def _running(settings, execution: str) -> bool | None:
    try:
        async with httpx.AsyncClient(base_url=settings.runner_url, timeout=5, headers={"authorization": f"Bearer {settings.runner_auth_token}"}) as client:
            response = await client.get(f"/v1/executions/{execution}")
        if response.status_code != 200:
            return None
        payload = response.json()
        return payload.get("state") == "running" if isinstance(payload, dict) else None
    except (httpx.HTTPError, ValueError):
        return None  # A Runner outage must not fabricate cancellation or approval.


async def reconcile(settings) -> None:
    """Resolve deadlines and stopped executions after restarts as well as live."""
    while True:
        try:
            now = time.time()
            with connect(settings.db_path, timeout=5) as conn:
                conn.execute("BEGIN IMMEDIATE")
                conn.execute("UPDATE runtime_interactions SET state='timed_out',outcome=timeout_outcome,updated_at=? WHERE state='pending' AND deadline IS NOT NULL AND deadline<=?", (now, now))
                executions = [row[0] for row in conn.execute("SELECT DISTINCT execution_id FROM runtime_interactions WHERE state='pending'")]
            for execution in executions:
                if await _running(settings, execution) is False:
                    changed = time.time()
                    with connect(settings.db_path, timeout=5) as conn:
                        conn.execute("UPDATE runtime_interactions SET state='cancelled',updated_at=? WHERE execution_id=? AND state='pending'", (changed, execution))
        except (sqlite3.Error, OSError):
            pass
        await asyncio.sleep(1)


def _apply_response(db_path: str, request_id: str, response: object) -> None:
    if not isinstance(response, dict) or set(response) != {"response_id", "chosen_value", "actor_user_id", "submitted_at"}:
        raise ValueError("Invalid hosted interaction response")
    if not all(isinstance(response.get(key), str) for key in response):
        raise ValueError("Invalid hosted interaction response")
    for key in ("response_id", "actor_user_id"):
        if str(uuid.UUID(response[key])) != response[key]:
            raise ValueError("Invalid hosted interaction response")
    with connect(db_path, timeout=5) as conn:
        conn.row_factory = sqlite3.Row
        conn.execute("BEGIN IMMEDIATE")
        row = conn.execute("SELECT * FROM runtime_interactions WHERE request_id=?", (request_id,)).fetchone()
        if row is None:
            raise ValueError("Unknown interaction acknowledgement")
        if row["state"] != "pending":
            return
        now = time.time()
        if row["deadline"] is not None and row["deadline"] <= now:
            conn.execute("UPDATE runtime_interactions SET state='timed_out',outcome=timeout_outcome,updated_at=? WHERE request_id=? AND state='pending'", (now, request_id))
            return
        choices = json.loads(row["choices_json"])
        if response["chosen_value"] not in {choice["value"] for choice in choices}:
            raise ValueError("Hosted response is not an interaction choice")
        confirmed = _timestamp(now)
        stored = {**response, "status": "applied", "confirmed_at": confirmed}
        conn.execute("UPDATE runtime_interactions SET state='answered',outcome=?,response_json=?,updated_at=? WHERE request_id=? AND state='pending'",
                     (response["chosen_value"], json.dumps(stored, separators=(",", ":")), now, request_id))


async def deliver(settings) -> None:
    """Replicate local truth, receive provisional answers, then confirm the winner."""
    async with httpx.AsyncClient(timeout=10) as client:
        while True:
            try:
                identity = load_identity(settings.db_path)
                if identity is not None and not credential_error(settings.credential_path):
                    with connect(settings.db_path) as conn:
                        conn.row_factory = sqlite3.Row
                        rows = conn.execute("SELECT * FROM runtime_interactions ORDER BY created_at LIMIT 100").fetchall()
                    for row in rows:
                        document = _document(row)
                        document.pop("robot_id")
                        response = document.pop("response")
                        document["applied_response_id"] = response["response_id"] if response else None
                        reply = await client.put(identity["platform_url"].rstrip("/") + f"/v1/device/interactions/{row['request_id']}",
                            headers=machine_headers(settings, identity), json=document)
                        reply.raise_for_status()
                        acknowledgement = reply.json()
                        if not isinstance(acknowledgement, dict) or set(acknowledgement) != {"request_id", "response"} or acknowledgement["request_id"] != row["request_id"]:
                            raise ValueError("Invalid interaction acknowledgement")
                        if acknowledgement["response"] is not None:
                            _apply_response(settings.db_path, row["request_id"], acknowledgement["response"])
            except (httpx.HTTPError, sqlite3.Error, OSError, ValueError, TypeError, KeyError):
                logger.warning("interaction synchronization failed; will retry")
            await asyncio.sleep(2)


@router.post("/v1/runtime/interactions")
async def create_interaction(request: Request):
    context = await _context(request)
    if context is None:
        return error(401, "invalid_token", "A valid execution credential is required.")
    execution, robot_id = context
    data = bytearray()
    async for chunk in request.stream():
        data.extend(chunk)
        if len(data) > 16384:
            return error(413, "size_limit", "Interaction request exceeds its limit.")
    try:
        value, deadline, fingerprint = _validate(data)
        running = await _running(request.app.state.settings, execution)
        if running is False:
            return error(401, "invalid_token", "The execution is no longer running.")
        if running is None:
            return error(503, "runtime_unavailable", "Runner state is unavailable.")
        now = time.time()
        with connect(request.app.state.settings.db_path, timeout=5) as conn:
            conn.row_factory = sqlite3.Row
            conn.execute("BEGIN IMMEDIATE")
            row = conn.execute("SELECT * FROM runtime_interactions WHERE request_id=?", (value["request_id"],)).fetchone()
            if row:
                if row["execution_id"] != execution or row["request_hash"] != fingerprint:
                    return error(409, "conflict", "request_id conflicts with an existing interaction.")
            else:
                conn.execute("INSERT INTO runtime_interactions VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)", (
                    value["request_id"], execution, robot_id, fingerprint, value["prompt"],
                    json.dumps(value["choices"], ensure_ascii=False, separators=(",", ":")), deadline,
                    value["timeout_outcome"], "pending", None, None, now, now))
                row = conn.execute("SELECT * FROM runtime_interactions WHERE request_id=?", (value["request_id"],)).fetchone()
        return _document(row)
    except (ValueError, TypeError, UnicodeError, OverflowError, RecursionError, json.JSONDecodeError):
        return error(400, "invalid_input", "Invalid interaction request.")
    except sqlite3.Error:
        return error(503, "runtime_unavailable", "Interaction storage is unavailable.")


@router.get("/v1/runtime/interactions/{request_id}")
async def get_interaction(request_id: str, request: Request):
    context = await _context(request)
    if context is None:
        return error(401, "invalid_token", "A valid execution credential is required.")
    try:
        value = _load(request.app.state.settings.db_path, request_id, context[0])
    except sqlite3.Error:
        return error(503, "runtime_unavailable", "Interaction storage is unavailable.")
    return value if value else error(404, "not_found", "Interaction not found.")


@router.get("/v1/runtime/interactions/{request_id}/wait")
async def wait_interaction(request_id: str, request: Request, wait_seconds: float = 30):
    context = await _context(request)
    if context is None:
        return error(401, "invalid_token", "A valid execution credential is required.")
    if not 0 <= wait_seconds <= 30:
        return error(400, "invalid_input", "wait_seconds must be between 0 and 30.")
    execution = context[0]
    until = time.monotonic() + wait_seconds
    try:
        while True:
            value = _load(request.app.state.settings.db_path, request_id, execution)
            if value is None:
                return error(404, "not_found", "Interaction not found.")
            if value["state"] != "pending" or time.monotonic() >= until:
                return value
            running = await _running(request.app.state.settings, execution)
            if running is False:
                now = time.time()
                with connect(request.app.state.settings.db_path, timeout=5) as conn:
                    conn.execute("UPDATE runtime_interactions SET state='cancelled',updated_at=? WHERE request_id=? AND execution_id=? AND state='pending'", (now, request_id, execution))
                return _load(request.app.state.settings.db_path, request_id, execution)
            await asyncio.sleep(min(0.25, max(0, until - time.monotonic())))
    except sqlite3.Error:
        return error(503, "runtime_unavailable", "Interaction storage is unavailable.")
