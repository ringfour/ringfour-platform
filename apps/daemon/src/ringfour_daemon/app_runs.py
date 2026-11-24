"""Durable hosted run dispatch; accepted commands never imply observed running."""

import asyncio
import hashlib
import json
import logging
import sqlite3
import uuid
from contextlib import closing
from datetime import UTC, datetime

import httpx
from jsonschema import ValidationError

from . import agent_inventory, app_installations, app_validation, operations
from .agent_inventory import utc_now
from .app_engine import ACTIVE, EngineError
from .db import connect

logger = logging.getLogger(__name__)


def begin(settings, executor, operation):
    command = app_validation.validate(operation.payload, "device", "start_command")
    with executor.mutex, closing(connect(settings.db_path)) as conn, conn:
        executor.fence()
        conn.execute("BEGIN IMMEDIATE")
        if (
            command["deployment_namespace"] != executor.identity["deployment_namespace"]
            or operation.robot_id != executor.identity["robot_id"]
        ):
            raise EngineError("ownership_conflict")
        known = conn.execute(
            "SELECT snapshot_json FROM app_runs WHERE run_id=?", (command["run_id"],)
        ).fetchone()
        if known:
            if json.loads(known[0]) != command["snapshot"]:
                raise EngineError("ownership_conflict")
            return
        current = app_installations.row(conn, command["installation_id"])
        snapshot = command["snapshot"]
        invalid = (
            not current
            or any(
                current[k] != snapshot[k]
                for k in ("app_id", "revision_id", "configuration_revision_id")
            )
            or json.loads(current["configuration_json"] or "null")
            != snapshot["configuration"]
            or json.loads(current["image_json"] or "{}").get("image")
            != snapshot["image"]
        )
        code = "stale_installation" if invalid else None
        try:
            app_installations.check_idle(conn)
        except app_installations.PreparationError:
            code = "execution_conflict"
        if current is None:
            conn.execute(
                "INSERT INTO app_installations(installation_id,robot_id,app_id,updated_at) VALUES (?,?,?,?)",
                (
                    command["installation_id"],
                    operation.robot_id,
                    snapshot["app_id"],
                    utc_now(),
                ),
            )
        failure = (
            None
            if code is None
            else app_validation.canonical(
                {
                    "code": code,
                    "message": "App start refused: " + code + ".",
                    "details": {"retryable": False},
                }
            )
        )
        conn.execute(
            """INSERT INTO app_runs(run_id,installation_id,robot_id,operation_id,app_id,revision_id,image_digest,configuration_revision_id,snapshot_json,state,requested_at,updated_at,failure_json,observed_at)
            VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
            (
                command["run_id"],
                command["installation_id"],
                operation.robot_id,
                operation.operation_id,
                snapshot["app_id"],
                snapshot["revision_id"],
                snapshot["image"].split("@", 1)[1],
                snapshot["configuration_revision_id"],
                app_validation.canonical(snapshot),
                "accepted" if code is None else "failed",
                utc_now(),
                utc_now(),
                failure,
                None if code is None else utc_now(),
            ),
        )


def fail(settings, executor, run_id, code, details=None):
    with executor.mutex, closing(connect(settings.db_path)) as conn, conn:
        executor.fence()
        failure = {
            "code": code,
            "message": "App start refused: " + code + ".",
            "details": details or {"retryable": False},
        }
        conn.execute(
            "UPDATE app_runs SET state='failed',failure_json=?,observed_at=?,updated_at=? WHERE run_id=? AND started_at IS NULL AND state='accepted'",
            (app_validation.canonical(failure), utc_now(), utc_now(), run_id),
        )


def stop(settings, executor, operation):
    c = app_validation.validate(operation.payload, "device", "stop_command")
    with executor.mutex:
        executor.fence()
        if (
            c["deployment_namespace"] != executor.identity["deployment_namespace"]
            or operation.robot_id != executor.identity["robot_id"]
        ):
            raise EngineError("ownership_conflict")
        with closing(connect(settings.db_path)) as conn, conn:
            conn.row_factory = sqlite3.Row
            row = conn.execute(
                "SELECT * FROM app_runs WHERE run_id=? AND installation_id=?",
                (c["expected_run_id"], c["installation_id"]),
            ).fetchone()
            if row is None:
                return  # Start command may not have replayed yet.
            intent = conn.execute(
                "SELECT phase FROM app_engine_intents WHERE run_id=?",
                (c["expected_run_id"],),
            ).fetchone()
            if row["state"] not in ACTIVE:
                return
            if intent is None or intent[0] in ("create", "created"):
                # Definitely never sent start. Leave any created container for exact cleanup.
                conn.execute(
                    "UPDATE app_runs SET state='stopped',observed_at=?,updated_at=?,stdout_sealed=1,stderr_sealed=1 WHERE run_id=?",
                    (utc_now(), utc_now(), row["run_id"]),
                )
                return
        executor.stop(c["expected_run_id"])


def publish(settings, executor, run_id):
    with executor.mutex, closing(connect(settings.db_path)) as conn, conn:
        executor.fence()
        conn.execute("BEGIN IMMEDIATE")
        conn.row_factory = sqlite3.Row
        row = dict(
            conn.execute("SELECT * FROM app_runs WHERE run_id=?", (run_id,)).fetchone()
        )
        observation = {
            k: row[k]
            for k in (
                "run_id",
                "installation_id",
                "state",
                "started_at",
                "ended_at",
                "exit_code",
            )
        }
        if observation["state"] == "starting":
            observation["state"] = "accepted"
        observation["failure"] = (
            None if row["failure_json"] is None else json.loads(row["failure_json"])
        )
        fingerprint = app_validation.digest(observation)
        previous = conn.execute(
            "SELECT fingerprint FROM app_run_reports WHERE run_id=?", (run_id,)
        ).fetchone()
        now = row["observed_at"] or utc_now()
        observation["observed_at"] = now
        reports = []
        for record in conn.execute(
            "SELECT * FROM device_operations WHERE kind IN ('app.start','app.stop') ORDER BY rowid"
        ).fetchall():
            payload = json.loads(record["payload"])
            if (
                payload.get("run_id", payload.get("expected_run_id")) != run_id
                or conn.execute(
                    "SELECT 1 FROM app_run_operation_reports WHERE operation_id=?",
                    (record["operation_id"],),
                ).fetchone()
            ):
                continue
            is_start = record["kind"] == "app.start"
            if (is_start and row["started_at"] is None and row["state"] in ACTIVE) or (
                not is_start and row["state"] in ACTIVE
            ):
                continue
            success = not is_start or row["started_at"] is not None
            failure = (
                None
                if success
                else observation["failure"]
                or {
                    "code": "execution_conflict",
                    "message": "Stopped before launch.",
                    "details": {"retryable": False},
                }
            )
            report = {
                "operation_id": record["operation_id"],
                "installation_id": row["installation_id"],
                "run_id": run_id,
                "kind": "start" if is_start else "stop",
                "state": "succeeded" if success else "failed",
                "phase": ("observed_running" if is_start else "stopped")
                if success
                else "failed",
                "observed_at": now,
                "failure": failure,
            }
            encoded = app_validation.canonical(report)
            conn.execute(
                "INSERT INTO app_run_operation_reports VALUES (?,?)",
                (record["operation_id"], encoded),
            )
            conn.execute(
                "UPDATE device_operations SET state=?,updated_at=? WHERE operation_id=?",
                (report["state"], now, record["operation_id"]),
            )
            reports.append(report)
        if previous and previous[0] == fingerprint and not reports:
            return
        sequence = conn.execute(
            "SELECT last_sequence+1 FROM app_observation_streams WHERE route='observations'"
        ).fetchone()[0]
        report = {
            "app_protocol_version": 1,
            "deployment_namespace": executor.identity["deployment_namespace"],
            "report_id": str(uuid.uuid4()),
            "report_sequence": sequence,
            "operations": reports,
            "installations": [],
            "runs": [observation],
        }
        app_validation.validate(report, "device", "observations")
        encoded = app_validation.canonical(report)
        conn.execute(
            "INSERT INTO app_observation_outbox(route,sequence,report_id,deployment_namespace,body_sha256,body_json,created_at) VALUES ('observations',?,?,?,?,?,?)",
            (
                sequence,
                report["report_id"],
                report["deployment_namespace"],
                hashlib.sha256(encoded.encode()).hexdigest(),
                encoded,
                utc_now(),
            ),
        )
        conn.execute(
            "INSERT INTO app_run_reports VALUES (?,?) ON CONFLICT(run_id) DO UPDATE SET fingerprint=excluded.fingerprint",
            (run_id, fingerprint),
        )


async def launch(settings, executor, operation, client):
    run_id = operation.payload["run_id"]
    row, intent = executor.row(run_id)
    if row["state"] not in ACTIVE:
        return
    if intent:
        await asyncio.to_thread(executor.start, run_id)
        return
    details = {"retryable": False}
    try:
        if operations.is_expired(operation.deadline_at, datetime.now(UTC)):
            raise EngineError("deadline_exceeded")
        snapshot = operation.payload["snapshot"]
        response = await client.get(
            "/v1/device/app-revisions/" + snapshot["revision_id"],
            params={"operation_id": operation.operation_id},
        )
        response.raise_for_status()
        revision = response.json()
        manifest = app_validation.validate(revision["manifest"], "manifest")
        if (
            revision["id"] != snapshot["revision_id"]
            or revision["app_id"] != snapshot["app_id"]
            or app_validation.digest(manifest) != snapshot["manifest_digest"]
        ):
            raise EngineError("revision_unavailable")
        app_validation.validate_configuration(manifest, snapshot["configuration"])
        checked = await agent_inventory.check_fresh(settings, manifest)
        if checked["status"] != "compatible":
            for key in ("missing_required", "missing_native"):
                if checked.get(key):
                    details[key] = checked[key]
            if checked["status"] == "inventory_unavailable":
                details["inventory_observed_at"] = checked.get("inventory_observed_at")
            if checked["status"] == "no_matching_image":
                details.update(
                    executor.engine.platform().model_dump(exclude={"native_facts"})
                )
            raise EngineError(checked["status"])
        if checked["target"]["image"] != snapshot["image"]:
            raise EngineError("revision_unavailable")
        await app_installations.diagnostic_idle(settings)
        # Stop may have arrived during the network/capability checks.
        with closing(connect(settings.db_path)) as conn:
            stopped = conn.execute(
                "SELECT 1 FROM device_operations WHERE kind='app.stop' AND json_extract(payload,'$.expected_run_id')=?",
                (run_id,),
            ).fetchone()
        if stopped:
            return
        await asyncio.to_thread(executor.start, run_id)
    except EngineError as error:
        if error.code == "engine_unavailable":
            raise
        if (
            error.code == "native_requirement_unavailable"
            and "missing_native" not in details
        ):
            details["missing_native"] = ["owner_workload_policy"]
        await asyncio.to_thread(fail, settings, executor, run_id, error.code, details)
    except (ValidationError, KeyError, TypeError, ValueError):
        await asyncio.to_thread(
            fail, settings, executor, run_id, "revision_unavailable"
        )


async def tick(settings, executor, headers):
    async with httpx.AsyncClient(
        base_url=executor.identity["platform_url"], headers=headers, timeout=15
    ) as client:
        await app_installations.deliver(settings, client)
        with closing(connect(settings.db_path)) as conn:
            starts = [
                operations.get(settings.db_path, r[0])
                for r in conn.execute(
                    "SELECT operation_id FROM device_operations WHERE kind='app.start' AND state IN ('accepted','running')"
                ).fetchall()
            ]
            stops = [
                operations.get(settings.db_path, r[0])
                for r in conn.execute(
                    "SELECT operation_id FROM device_operations WHERE kind='app.stop' AND state IN ('accepted','running')"
                ).fetchall()
            ]
        for operation in starts:
            await asyncio.to_thread(begin, settings, executor, operation)
        for operation in stops:
            await asyncio.to_thread(stop, settings, executor, operation)
        for operation in starts:
            await launch(settings, executor, operation, client)
        with closing(connect(settings.db_path)) as conn:
            runs = [
                r[0] for r in conn.execute("SELECT run_id FROM app_runs").fetchall()
            ]
        for run_id in runs:
            await asyncio.to_thread(publish, settings, executor, run_id)
        await app_installations.deliver(settings, client)


async def run(settings, manager):
    while True:
        if (
            manager.app_executor
            and manager.connected
            and hasattr(manager, "app_headers")
        ):
            try:
                await tick(settings, manager.app_executor, manager.app_headers)
            except (httpx.HTTPError, ValueError, sqlite3.Error):
                logger.warning(
                    "App run reconciliation/delivery pending; retaining intent."
                )
        await asyncio.sleep(0.5)
