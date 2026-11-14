"""Development run control and metadata outbox; output stays on the robot."""
import asyncio
import logging
import sqlite3

import httpx

from . import runner
from .enrollment import credential_error, load_identity
from .scripts import machine_headers, valid_id

logger = logging.getLogger(__name__)


def record(db_path, payload):
    with sqlite3.connect(db_path) as conn:
        conn.execute("BEGIN IMMEDIATE")
        existing = conn.execute("SELECT session_id,operation_id FROM local_dev_runs WHERE execution_id=?", (payload["execution_id"],)).fetchone()
        if existing and existing != (payload["session_id"], payload["operation_id"]):
            raise runner.RunnerRejected("conflict", "Execution ID was reused.")
        if not existing:
            conn.execute("INSERT INTO local_dev_runs(execution_id,session_id,operation_id) VALUES (?,?,?)", (payload["execution_id"], payload["session_id"], payload["operation_id"]))


def belongs(db_path, session_id, execution_id):
    if not valid_id(session_id) or not valid_id(execution_id):
        return False
    with sqlite3.connect(db_path) as conn:
        return conn.execute("SELECT 1 FROM local_dev_runs WHERE session_id=? AND execution_id=?", (session_id, execution_id)).fetchone() is not None


def fail_unstarted(db_path, operation_id):
    with sqlite3.connect(db_path) as conn:
        conn.execute("UPDATE local_dev_runs SET completed=1 WHERE operation_id=? AND started=0", (operation_id,))


async def restart(settings, operation):
    payload = operation.payload
    keys = {"operation_id", "session_id", "robot_id", "generation", "execution_id"}
    if (not isinstance(payload, dict) or set(payload) != keys or payload["operation_id"] != operation.operation_id
            or payload["robot_id"] != operation.robot_id or not all(valid_id(payload[key]) for key in ("operation_id", "session_id", "robot_id", "execution_id"))
            or type(payload["generation"]) is not int or not 1 <= payload["generation"] <= 20):
        raise runner.RunnerRejected("invalid_input", "Invalid development restart.")
    if not settings.runner_auth_token:
        raise runner.RunnerRejected("runner_unauthenticated", "Runner credential unavailable.")
    record(settings.db_path, payload)
    async with httpx.AsyncClient(base_url=settings.runner_url, headers={"authorization": f"Bearer {settings.runner_auth_token}"}, timeout=20) as client:
        response = await client.post("/v1/dev-sessions/restart", json=payload)
        if response.status_code >= 500:
            response.raise_for_status()
        if response.status_code != 200:
            runner.reject_response(response)
        try:
            result = response.json()
        except ValueError as exc:
            raise httpx.HTTPError("Invalid Runner response; reconcile the same operation.") from exc
        if (not isinstance(result, dict) or result.get("session_id") != payload["session_id"] or result.get("generation") != payload["generation"]
                or not isinstance(result.get("execution"), dict) or result["execution"].get("execution_id") != payload["execution_id"]):
            raise httpx.HTTPError("Invalid Runner response; reconcile the same operation.")
    with sqlite3.connect(settings.db_path) as conn:
        conn.execute("UPDATE local_dev_runs SET started=1 WHERE execution_id=?", (payload["execution_id"],))
    return result


async def report(settings, identity, session_id, execution_id, started):
    headers = {"authorization": f"Bearer {settings.runner_auth_token}"}
    async with httpx.AsyncClient(base_url=settings.runner_url, headers=headers, timeout=10) as local:
        response = await local.get(f"/v1/executions/{execution_id}")
        if response.status_code == 404:
            if not started:
                return
            status = {"state": "failed", "exit_code": None, "error": {"code": "interrupted_unknown_outcome", "message": "Runner lost the recorded execution."}}
        else:
            response.raise_for_status()
            snapshot = response.json()
            status = {key: snapshot[key] for key in ("state", "exit_code", "error")}
    async with httpx.AsyncClient(base_url=identity["platform_url"], headers=machine_headers(settings, identity), timeout=10) as platform:
        response = await platform.put(f"/v1/device/dev-sessions/{session_id}/runs/{execution_id}", json=status)
        response.raise_for_status()
    if status["state"] in ("succeeded", "failed", "cancelled"):
        with sqlite3.connect(settings.db_path) as conn:
            conn.execute("UPDATE local_dev_runs SET completed=1 WHERE execution_id=?", (execution_id,))


async def reconcile(settings):
    while True:
        try:
            identity = load_identity(settings.db_path)
            if identity is not None and not credential_error(settings.credential_path) and settings.runner_auth_token:
                with sqlite3.connect(settings.db_path) as conn:
                    rows = conn.execute("SELECT session_id,execution_id,started FROM local_dev_runs WHERE completed=0 ORDER BY rowid LIMIT 100").fetchall()
                for session_id, execution_id, started in rows:
                    try:
                        await report(settings, identity, session_id, execution_id, bool(started))
                    except (httpx.HTTPError, OSError, ValueError, KeyError, TypeError, sqlite3.Error):
                        logger.warning("Development execution status synchronization failed; retrying.")
        except (sqlite3.Error, OSError):
            logger.warning("Development execution reconciliation unavailable; retrying.")
        await asyncio.sleep(1)
