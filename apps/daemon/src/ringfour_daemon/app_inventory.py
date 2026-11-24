"""Negotiate App support and serially deliver durable inventory reports."""

import asyncio
import hashlib
import json
import logging
import sqlite3
import uuid
from contextlib import closing

import httpx

from .agent_inventory import (
    InventoryUnavailable,
    engine_platform,
    observe_agent,
    utc_now,
)
from .app_protocol import (
    Id,
    InventoryReport,
    NegotiationResponse,
    ProtocolModel,
    Sequence,
    Timestamp,
    parse,
)
from .db import connect

logger = logging.getLogger(__name__)


class ReportAccepted(ProtocolModel):
    report_id: Id
    report_sequence: Sequence
    received_at: Timestamp


def pending_report(db_path):
    with closing(connect(db_path)) as conn:
        row = conn.execute("SELECT body_json FROM app_observation_outbox WHERE route='inventory' AND acknowledged_at IS NULL ORDER BY sequence LIMIT 1").fetchone()
    return None if row is None else json.loads(row[0])


def last_successful(db_path):
    with closing(connect(db_path)) as conn:
        row = conn.execute("SELECT last_successful_json FROM app_inventory_observation WHERE singleton_id=1").fetchone()
    return None if not row or not row[0] else json.loads(row[0])


def queue_report(db_path, namespace, platform, inventory):
    with closing(connect(db_path)) as conn, conn:
        conn.execute("BEGIN IMMEDIATE")
        pending = conn.execute("SELECT body_json FROM app_observation_outbox WHERE route='inventory' AND acknowledged_at IS NULL ORDER BY sequence LIMIT 1").fetchone()
        if pending:
            return json.loads(pending[0])
        sequence = conn.execute("SELECT last_sequence + 1 FROM app_observation_streams WHERE route='inventory'").fetchone()[0]
        report = InventoryReport.model_validate({
            "app_protocol_version": 1, "deployment_namespace": namespace,
            "report_id": str(uuid.uuid4()), "report_sequence": sequence,
            "platform": platform.model_dump(), "inventory": inventory,
        }).model_dump()
        encoded = json.dumps(report, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
        # Byte limits and semantic validations run before any durable projection update.
        parse(InventoryReport, encoded)
        conn.execute("INSERT INTO app_observation_outbox(route,sequence,report_id,deployment_namespace,body_sha256,body_json,created_at) VALUES ('inventory',?,?,?,?,?,?)",
                     (sequence, report["report_id"], namespace, hashlib.sha256(encoded.encode()).hexdigest(), encoded, utc_now()))
        successful = inventory.get("current") if inventory["state"] == "available" else inventory["last_successful"]
        conn.execute("""INSERT INTO app_inventory_observation
            (singleton_id,deployment_namespace,state,platform,inventory_json,observed_at,last_successful_at,unavailable_reason,last_successful_json)
            VALUES (1,?,?,?,?,?,?,?,?) ON CONFLICT(singleton_id) DO UPDATE SET
            state=excluded.state,platform=excluded.platform,inventory_json=excluded.inventory_json,
            observed_at=excluded.observed_at,last_successful_at=excluded.last_successful_at,
            unavailable_reason=excluded.unavailable_reason,last_successful_json=excluded.last_successful_json""",
            (namespace, inventory["state"], json.dumps(platform.model_dump()) if inventory["state"] == "available" else None,
             json.dumps(inventory["current"]) if inventory["state"] == "available" else None,
             inventory["attempted_at"], None if successful is None else successful["observed_at"],
             inventory.get("reason"), None if successful is None else json.dumps(successful)))
        return report


def acknowledge(db_path, report, receipt):
    if receipt.report_id != report["report_id"] or receipt.report_sequence != report["report_sequence"]:
        raise ValueError("inventory acknowledgement does not match report")
    with closing(connect(db_path)) as conn, conn:
        conn.execute("UPDATE app_observation_outbox SET acknowledged_at=?,acknowledgement_json=? WHERE route='inventory' AND report_id=? AND acknowledged_at IS NULL",
                     (receipt.received_at, json.dumps(receipt.model_dump()), receipt.report_id))


async def report_once(settings, identity, client, platform):
    report = await asyncio.to_thread(pending_report, settings.db_path)
    if report is None:
        inventory = {"state": "available", "attempted_at": utc_now()}
        try:
            inventory["current"] = (await observe_agent(settings.agent_uri, settings.agent_inventory_timeout_seconds)).model_dump()
        except InventoryUnavailable as error:
            inventory = {"state": "unavailable", "attempted_at": inventory["attempted_at"],
                         "reason": error.reason, "last_successful": await asyncio.to_thread(last_successful, settings.db_path)}
        report = await asyncio.to_thread(queue_report, settings.db_path, identity["deployment_namespace"], platform, inventory)
    response = await client.put("/v1/device/app-inventory", json=report)
    response.raise_for_status()
    receipt = parse(ReportAccepted, response.text)
    await asyncio.to_thread(acknowledge, settings.db_path, report, receipt)
    return report


async def run_session(settings, identity, session_id, headers):
    if not settings.agent_uri:
        return
    try:
        platform = engine_platform(settings)
        if not identity.get("deployment_namespace"):
            raise ValueError("enrollment has no deployment namespace")
        if settings.app_inventory_interval_seconds <= 0:
            raise ValueError("inventory interval must be positive")
    except (ValueError, InventoryUnavailable):
        logger.warning("App inventory disabled: configure the Agent URI and verified engine platform/namespace.")
        return
    async with httpx.AsyncClient(base_url=identity["platform_url"], headers=headers, timeout=10) as client:
        offer = {"session_id": session_id, "deployment_namespace": identity["deployment_namespace"],
                 "supported_app_protocol_versions": [1]}
        if settings.apps_enabled:
            offer["supports_app_installations"] = True
        while True:
            try:
                response = await client.post("/v1/device/app-protocol/negotiate", json=offer)
                if response.status_code in (404, 409):
                    logger.warning("App inventory negotiation refused (%d); legacy transport stays available.", response.status_code)
                    return
                response.raise_for_status()
                chosen = parse(NegotiationResponse, response.text)
                if chosen.session_id != session_id or chosen.deployment_namespace != identity["deployment_namespace"]:
                    raise ValueError("App negotiation identity mismatch")
                break
            except (httpx.HTTPError, ValueError):
                logger.warning("App inventory negotiation unavailable; retrying.")
                await asyncio.sleep(settings.app_inventory_interval_seconds)
        client.headers["x-ringfour-device-session"] = session_id
        while True:
            try:
                await report_once(settings, identity, client, platform)
            except (httpx.HTTPError, ValueError, sqlite3.Error):
                logger.warning("App inventory delivery failed; retaining the durable report for retry.")
            await asyncio.sleep(settings.app_inventory_interval_seconds)
