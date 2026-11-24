"""Durable installation preparation/application; never creates a container or run."""

import asyncio
import hashlib
import json
import re
import sqlite3
import uuid
from contextlib import closing
from datetime import UTC, datetime
from functools import wraps

import httpx
from jsonschema.exceptions import ValidationError

from . import agent_inventory, app_validation, operations
from .agent_inventory import utc_now
from .app_engine import EngineError
from .app_inventory import ReportAccepted
from .app_protocol import parse
from .db import connect


class PreparationError(ValueError):
    def __init__(self, code, details=None):
        self.code = code
        self.details = details or {"retryable": False}
        super().__init__(code)


def installed(row):
    if row["revision_id"] is None:
        return None
    return {
        key: row[key]
        for key in (
            "revision_id",
            "configuration_revision_id",
            "applied_operation_id",
            "installed_at",
        )
    } | {"image": json.loads(row["image_json"])["image"]}


def row(conn, installation):
    conn.row_factory = sqlite3.Row
    value = conn.execute(
        "SELECT * FROM app_installations WHERE installation_id=?", (installation,)
    ).fetchone()
    return None if value is None else dict(value)


def check_generation(current, command):
    if (None if current is None else current["revision_id"]) != command[
        "expected_revision_id"
    ] or (None if current is None else current["configuration_revision_id"]) != command[
        "expected_configuration_revision_id"
    ]:
        raise PreparationError("stale_installation")


def check_idle(conn):
    if (
        conn.execute("SELECT 1 FROM device_execution_slots").fetchone()
        or conn.execute("SELECT 1 FROM local_dev_runs WHERE completed=0").fetchone()
    ):
        raise PreparationError("execution_conflict")


def candidate(operation):
    release = operation.payload["release"]
    return {
        "operation_id": operation.operation_id,
        "revision_id": release["revision_id"],
        "configuration_revision_id": release["configuration_revision_id"],
        "image": release["target"]["image"],
        "state": "staging",
        "staged_at": None,
        "failure": None,
    }


def fenced(action):
    @wraps(action)
    def call(settings, executor, *args, **kwargs):
        with executor.mutex:
            executor.fence()
            return action(settings, executor, *args, **kwargs)

    return call


@fenced
def begin(settings, executor, operation):
    executor.fence()
    command = app_validation.validate(operation.payload, "device", "apply_command")
    if (
        command["deployment_namespace"] != executor.identity["deployment_namespace"]
        or operation.robot_id != executor.identity["robot_id"]
    ):
        raise PreparationError("ownership_conflict")
    with closing(connect(settings.db_path)) as conn, conn:
        conn.execute("BEGIN IMMEDIATE")
        known = conn.execute(
            "SELECT state FROM app_preparations WHERE operation_id=?",
            (operation.operation_id,),
        ).fetchone()
        if known:
            return known[0]
        current = row(conn, command["installation_id"])
        check_generation(current, command)
        if current is not None and (
            current["robot_id"] != operation.robot_id
            or current["app_id"] != command["release"]["app_id"]
        ):
            raise PreparationError("ownership_conflict")
        if current is not None and current["candidate_json"]:
            old = json.loads(current["candidate_json"])
            if (
                old["state"] in ("staging", "staged")
                and old["operation_id"] != operation.operation_id
            ):
                raise PreparationError("stale_installation")
        now = utc_now()
        if current is None:
            conn.execute(
                "INSERT INTO app_installations(installation_id,robot_id,app_id,updated_at) VALUES (?,?,?,?)",
                (
                    command["installation_id"],
                    operation.robot_id,
                    command["release"]["app_id"],
                    now,
                ),
            )
        conn.execute(
            "INSERT INTO app_preparations VALUES (?,?,?,NULL,?)",
            (operation.operation_id, command["installation_id"], "staging", now),
        )
        conn.execute(
            "UPDATE app_installations SET candidate_json=?,updated_at=? WHERE installation_id=?",
            (
                app_validation.canonical(candidate(operation)),
                now,
                command["installation_id"],
            ),
        )
        return "staging"


@fenced
def stage(settings, executor, operation):
    with closing(connect(settings.db_path)) as conn, conn:
        c = candidate(operation)
        c["state"] = "staged"
        c["staged_at"] = utc_now()
        conn.execute(
            "UPDATE app_preparations SET state='staged',updated_at=? WHERE operation_id=?",
            (utc_now(), operation.operation_id),
        )
        conn.execute(
            "UPDATE app_installations SET candidate_json=?,updated_at=? WHERE installation_id=?",
            (
                app_validation.canonical(c),
                utc_now(),
                operation.payload["installation_id"],
            ),
        )


@fenced
def finish(settings, executor, operation, failure=None):
    executor.fence()
    command = operation.payload
    release = command["release"]
    now = utc_now()
    with closing(connect(settings.db_path)) as conn, conn:
        conn.execute("BEGIN IMMEDIATE")
        saved = conn.execute(
            "SELECT terminal_report_json FROM app_preparations WHERE operation_id=?",
            (operation.operation_id,),
        ).fetchone()
        if saved and saved[0]:
            return json.loads(saved[0])
        current = row(conn, command["installation_id"])
        check_generation(current, command)
        if (
            not current
            or not current["candidate_json"]
            or json.loads(current["candidate_json"])["operation_id"]
            != operation.operation_id
        ):
            raise PreparationError("stale_installation")
        if failure is None:
            check_idle(conn)
            if operations.is_expired(operation.deadline_at, datetime.now(UTC)):
                raise PreparationError("deadline_exceeded", {"retryable": True})
            conn.execute(
                "UPDATE app_installations SET revision_id=?,configuration_revision_id=?,image_json=?,configuration_json=?,applied_operation_id=?,installed_at=?,observed_at=?,candidate_json=NULL,updated_at=? WHERE installation_id=?",
                (
                    release["revision_id"],
                    release["configuration_revision_id"],
                    app_validation.canonical(release["target"]),
                    app_validation.canonical(release["configuration"]),
                    operation.operation_id,
                    now,
                    now,
                    now,
                    command["installation_id"],
                ),
            )
        else:
            failed = candidate(operation)
            failed["state"] = "failed"
            failed["failure"] = failure
            conn.execute(
                "UPDATE app_installations SET candidate_json=?,observed_at=?,updated_at=? WHERE installation_id=?",
                (
                    app_validation.canonical(failed),
                    now,
                    now,
                    command["installation_id"],
                ),
            )
        current = row(conn, command["installation_id"])
        sequence = conn.execute(
            "SELECT last_sequence+1 FROM app_observation_streams WHERE route='observations'"
        ).fetchone()[0]
        state = "succeeded" if failure is None else "failed"
        report = {
            "app_protocol_version": 1,
            "deployment_namespace": executor.identity["deployment_namespace"],
            "report_id": str(uuid.uuid4()),
            "report_sequence": sequence,
            "operations": [
                {
                    "operation_id": operation.operation_id,
                    "installation_id": command["installation_id"],
                    "run_id": None,
                    "kind": operation.kind.removeprefix("app."),
                    "state": state,
                    "phase": "applied" if failure is None else "failed",
                    "observed_at": now,
                    "failure": failure,
                }
            ],
            "installations": [
                {
                    "installation_id": command["installation_id"],
                    "observed_at": now,
                    "installed": installed(current),
                    "candidate": None
                    if failure is None
                    else json.loads(current["candidate_json"]),
                }
            ],
            "runs": [],
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
                now,
            ),
        )
        conn.execute(
            "UPDATE app_preparations SET state=?,terminal_report_json=?,updated_at=? WHERE operation_id=?",
            (
                "applied" if failure is None else "failed",
                encoded,
                now,
                operation.operation_id,
            ),
        )
        conn.execute(
            "UPDATE device_operations SET state=?,result=?,updated_at=? WHERE operation_id=?",
            (
                state,
                app_validation.canonical({"report_id": report["report_id"]}),
                now,
                operation.operation_id,
            ),
        )
        return report


async def deliver(settings, client):
    while True:
        with closing(connect(settings.db_path)) as conn:
            value = conn.execute(
                "SELECT body_json FROM app_observation_outbox WHERE route='observations' AND acknowledged_at IS NULL ORDER BY sequence LIMIT 1"
            ).fetchone()
        if value is None:
            return
        report = json.loads(value[0])
        response = await client.put("/v1/device/app-observations", json=report)
        response.raise_for_status()
        receipt = parse(ReportAccepted, response.text).model_dump()
        if (
            receipt["report_id"] != report["report_id"]
            or receipt["report_sequence"] != report["report_sequence"]
        ):
            raise ValueError("Observation receipt mismatch")
        with closing(connect(settings.db_path)) as conn, conn:
            conn.execute(
                "UPDATE app_observation_outbox SET acknowledged_at=?,acknowledgement_json=? WHERE report_id=? AND acknowledged_at IS NULL",
                (
                    receipt["received_at"],
                    app_validation.canonical(receipt),
                    report["report_id"],
                ),
            )


async def diagnostic_idle(settings):
    with closing(connect(settings.db_path)) as conn:
        pending = conn.execute(
            "SELECT 1 FROM device_operations WHERE kind='diagnostic.execution.start' AND state IN ('accepted','running')"
        ).fetchone()
        latest = conn.execute(
            "SELECT operation_id FROM device_operations WHERE kind='diagnostic.execution.start' AND state='succeeded' ORDER BY rowid DESC LIMIT 1"
        ).fetchone()
    if pending:
        raise PreparationError("execution_conflict")
    if latest:
        async with httpx.AsyncClient(
            base_url=settings.runner_url,
            headers={"authorization": "Bearer " + (settings.runner_auth_token or "")},
            timeout=3,
        ) as client:
            response = await client.get("/v1/executions/" + latest[0])
            if response.status_code != 404:
                response.raise_for_status()
                if response.json()["state"] in ("queued", "preparing", "running"):
                    raise PreparationError("execution_conflict")


async def process(settings, executor, operation, headers):
    if not settings.apps_enabled or executor is None:
        raise PreparationError("daemon_upgrade_required")
    app_validation.validate(operation.payload, "device", "apply_command")
    async with httpx.AsyncClient(
        base_url=executor.identity["platform_url"], headers=headers, timeout=30
    ) as client:
        await deliver(settings, client)
        state = await asyncio.to_thread(begin, settings, executor, operation)
        if state in ("applied", "failed"):
            return
        try:
            if operations.is_expired(operation.deadline_at, datetime.now(UTC)):
                raise PreparationError("deadline_exceeded", {"retryable": True})
            release = operation.payload["release"]
            response = await client.get(
                "/v1/device/app-revisions/" + release["revision_id"],
                params={"operation_id": operation.operation_id},
            )
            response.raise_for_status()
            revision = app_validation.raw_json(response.text)
            try:
                manifest = app_validation.validate(revision["manifest"], "manifest")
            except (ValidationError, ValueError, KeyError) as error:
                raise PreparationError("revision_unavailable") from error
            if (
                app_validation.digest(manifest) != release["manifest_digest"]
                or revision["id"] != release["revision_id"]
                or revision["app_id"] != release["app_id"]
                or release["target"] not in manifest["targets"]
            ):
                raise PreparationError("revision_unavailable")
            app_validation.validate_configuration(manifest, release["configuration"])
            await asyncio.to_thread(executor.engine.platform)
            checked = await agent_inventory.check_fresh(settings, manifest)
            if checked["status"] != "compatible":
                details = {"retryable": checked["status"] == "inventory_unavailable"}
                if checked["status"] == "inventory_unavailable":
                    details["inventory_observed_at"] = checked["inventory_observed_at"]
                if checked["status"] == "missing_required":
                    details["missing_required"] = checked["missing_required"]
                if checked["status"] == "native_requirement_unavailable":
                    details["missing_native"] = checked["missing_native"]
                if checked["status"] == "no_matching_image":
                    details.update(
                        executor.engine.platform().model_dump(exclude={"native_facts"})
                    )
                raise PreparationError(checked["status"], details)
            if checked["target"] != release["target"]:
                raise PreparationError(
                    "no_matching_image",
                    {
                        "retryable": False,
                        **executor.engine.platform().model_dump(
                            exclude={"native_facts"}
                        ),
                    },
                )
            await diagnostic_idle(settings)
            if state != "staged" and operation.kind != "app.configure":
                response = await client.post(
                    "/v1/device/app-registry-grants",
                    json={
                        "operation_id": operation.operation_id,
                        "revision_id": release["revision_id"],
                        "image": release["target"]["image"],
                    },
                )
                response.raise_for_status()
                grant = app_validation.raw_json(response.text)
                try:
                    app_validation.validate(grant, "api", "registry_grant")
                except ValidationError as error:
                    raise PreparationError("revision_unavailable") from error
                authority, repository = (
                    release["target"]["image"].split("@", 1)[0].split("/", 1)
                )
                if (
                    grant["registry"] != authority
                    or grant["repository"] != repository
                    or datetime.fromisoformat(grant["expires_at"]) <= datetime.now(UTC)
                ):
                    raise PreparationError("revision_unavailable", {"retryable": True})
                if any(
                    grant[key] != value
                    for key, value in {
                        "operation_id": operation.operation_id,
                        "robot_id": operation.robot_id,
                        "revision_id": release["revision_id"],
                        "image": release["target"]["image"],
                    }.items()
                ):
                    raise PreparationError("revision_unavailable")
                image = await asyncio.to_thread(
                    executor.prepare_image, release["target"], grant["authorization"]
                )
            else:
                image = await asyncio.to_thread(
                    executor.engine.image, release["target"]["image"]
                )
                if not image or release["target"]["image"] not in image.get(
                    "RepoDigests", []
                ):
                    raise PreparationError("revision_unavailable")
            if (image.get("Os"), image.get("Architecture")) != (
                release["target"]["os"],
                release["target"]["architecture"],
            ):
                raise PreparationError("revision_unavailable")
            config = image.get("Config", {})
            if not config.get("Entrypoint") or not re.fullmatch(
                r"[1-9][0-9]*(?::[0-9]+)?", config.get("User", "")
            ):
                raise PreparationError(
                    "native_requirement_unavailable",
                    {"retryable": False, "missing_native": ["nonroot_entrypoint"]},
                )
            await asyncio.to_thread(stage, settings, executor, operation)
            await asyncio.to_thread(finish, settings, executor, operation)
        except (PreparationError, EngineError, ValidationError, ValueError) as error:
            code = (
                error.code
                if isinstance(error, (PreparationError, EngineError))
                else "invalid_config"
            )
            details = (
                error.details
                if isinstance(error, PreparationError)
                else {"retryable": isinstance(error, EngineError)}
            )
            await asyncio.to_thread(
                finish,
                settings,
                executor,
                operation,
                {
                    "code": code,
                    "message": "App preparation failed: " + code + ".",
                    "details": details,
                },
            )
        await deliver(settings, client)
