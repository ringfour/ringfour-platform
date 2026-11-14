"""Durable bounded Logbook outbox; no operation lifecycle producer yet."""
import asyncio
import json
import logging
import sqlite3
import uuid
from datetime import UTC, datetime

import httpx

from .enrollment import credential_error, load_identity
from .scripts import machine_headers

logger = logging.getLogger(__name__)
MAX_EVENTS = 4096
MAX_EVENT_BYTES = 12288
MAX_BATCH_BYTES = 65536
MAX_BATCH_EVENTS = 100
MAX_SEQUENCE = 9007199254740991
STATES = frozenset({"accepted", "running", "succeeded", "failed", "cancelled"})


class OutboxError(ValueError):
    """The stream cannot safely accept or acknowledge an event."""


class OutboxFull(OutboxError):
    """The bounded queue is full; caller must not commit its mutation."""


def _id(value):
    try:
        return isinstance(value, str) and str(uuid.UUID(value)) == value
    except ValueError:
        return False


def append(conn, robot_id, operation_id, state, *, now=None, event_id=None, execution_id=None, author_payload=None, motion_payload=None):
    """Append within the caller's transaction; the caller owns commit/rollback."""
    if not conn.in_transaction:
        raise OutboxError("A write transaction is required.")
    event_id = event_id or str(uuid.uuid4())
    author = author_payload is not None
    motion = motion_payload is not None
    if (not all(_id(item) for item in (robot_id, event_id)) or (author and motion)
            or (motion and (operation_id is not None or state is not None or execution_id is not None
                            or not isinstance(motion_payload, dict) or motion_payload.get('event_id') != event_id))
            or (not motion and not _id(operation_id))
            or (author and (state is not None or not _id(execution_id)))
            or (not author and not motion and (state not in STATES or execution_id is not None))):
        raise OutboxError("Invalid event.")
    stamp = now or datetime.now(UTC)
    if stamp.tzinfo is None or stamp.utcoffset() is None:
        raise OutboxError("An aware timestamp is required.")
    row = conn.execute("SELECT robot_id,last_sequence FROM logbook_outbox_state WHERE singleton_id=1").fetchone()
    if row and row[0] != robot_id:
        raise OutboxError("The event stream belongs to another robot.")
    if conn.execute("SELECT count(*) FROM logbook_outbox").fetchone()[0] >= MAX_EVENTS:
        raise OutboxFull("Logbook outbox is full.")
    sequence = (row[1] if row else 0) + 1
    if sequence > MAX_SEQUENCE:
        raise OutboxFull("Logbook sequence is exhausted.")
    event = {"event_id": event_id, "robot_id": robot_id, "local_sequence": sequence,
             "occurred_at": stamp.astimezone(UTC).isoformat(timespec="microseconds").replace("+00:00", "Z"),
             "type": "mission.logbook.entry" if author else "agent.motion.event" if motion else "device.operation.state_changed", "schema_version": 1,
             "operation_id": operation_id, "payload": author_payload if author else motion_payload if motion else {"state": state}}
    if author:
        event["execution_id"] = execution_id
    encoded = json.dumps(event, sort_keys=True, ensure_ascii=False, separators=(",", ":"))
    if len(encoded.encode()) > MAX_EVENT_BYTES:
        raise OutboxError("Event exceeds its byte limit.")
    conn.execute("SAVEPOINT logbook_append")
    try:
        if row:
            conn.execute("UPDATE logbook_outbox_state SET last_sequence=? WHERE singleton_id=1", (sequence,))
        else:
            conn.execute("INSERT INTO logbook_outbox_state VALUES (1,?,?,0)", (robot_id, sequence))
        conn.execute("INSERT INTO logbook_outbox VALUES (?,?,?)", (sequence, event_id, encoded))
        conn.execute("RELEASE SAVEPOINT logbook_append")
    except sqlite3.Error:
        conn.execute("ROLLBACK TO SAVEPOINT logbook_append")
        conn.execute("RELEASE SAVEPOINT logbook_append")
        raise
    return event


def batch(db_path, robot_id):
    with sqlite3.connect(db_path) as conn:
        state = conn.execute("SELECT robot_id,acknowledged_sequence FROM logbook_outbox_state WHERE singleton_id=1").fetchone()
        if not state:
            return []
        if state[0] != robot_id:
            raise OutboxError("The event stream belongs to another robot.")
        rows = conn.execute("SELECT local_sequence,event_json FROM logbook_outbox ORDER BY local_sequence LIMIT ?", (MAX_BATCH_EVENTS,)).fetchall()
    events = []
    expected = state[1] + 1
    for sequence, encoded in rows:
        if sequence != expected:
            raise OutboxError("The local event stream contains a gap.")
        event = json.loads(encoded)
        candidate = {"events": [*events, event]}
        if len(json.dumps(candidate, ensure_ascii=False, separators=(",", ":")).encode()) > MAX_BATCH_BYTES:
            break
        events.append(event)
        expected += 1
    return events


def acknowledge(db_path, robot_id, sent, checkpoint):
    if not sent or type(checkpoint) is not int:
        raise OutboxError("Invalid acknowledgement.")
    with sqlite3.connect(db_path) as conn:
        conn.execute("BEGIN IMMEDIATE")
        state = conn.execute("SELECT robot_id,last_sequence,acknowledged_sequence FROM logbook_outbox_state WHERE singleton_id=1").fetchone()
        if (not state or state[0] != robot_id or checkpoint < sent[-1]["local_sequence"]
                or checkpoint < state[2] or checkpoint > state[1]):
            raise OutboxError("Invalid acknowledgement.")
        conn.execute("UPDATE logbook_outbox_state SET acknowledged_sequence=? WHERE singleton_id=1", (checkpoint,))
        conn.execute("DELETE FROM logbook_outbox WHERE local_sequence<=?", (checkpoint,))


async def deliver_once(settings, client):
    identity = load_identity(settings.db_path)
    if identity is None or credential_error(settings.credential_path):
        return False
    events = batch(settings.db_path, identity["robot_id"])
    if not events:
        return False
    response = await client.post(identity["platform_url"].rstrip("/") + "/v1/device/logbook/events",
                                 headers=machine_headers(settings, identity), json={"events": events})
    response.raise_for_status()
    data = response.json()
    if not isinstance(data, dict) or set(data) != {"acknowledged_sequence"}:
        raise OutboxError("Invalid acknowledgement.")
    acknowledge(settings.db_path, identity["robot_id"], events, data["acknowledged_sequence"])
    return True


async def deliver(settings):
    async with httpx.AsyncClient(timeout=10.0) as client:
        while True:
            try:
                if await deliver_once(settings, client):
                    continue
            except (httpx.HTTPError, OSError, ValueError, sqlite3.Error):
                logger.warning("Logbook delivery failed; queued events will be retried.")
            await asyncio.sleep(2)
