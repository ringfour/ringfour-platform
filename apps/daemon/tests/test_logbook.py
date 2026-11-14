import asyncio
import json
import sqlite3
import uuid

import httpx
import pytest

from ringfour_daemon import logbook
from ringfour_daemon.config import Settings
from ringfour_daemon.db import migrate


def identity():
    return str(uuid.uuid4())


def setup(tmp_path):
    path = str(tmp_path / "daemon.db")
    migrate(path)
    return path


def append(path, robot, operation, state="accepted"):
    with sqlite3.connect(path) as conn:
        conn.execute("BEGIN IMMEDIATE")
        return logbook.append(conn, robot, operation, state)


def test_outbox_transaction_reopen_capacity_and_acknowledgement(tmp_path, monkeypatch):
    db = setup(tmp_path)
    robot, operation = identity(), identity()
    with sqlite3.connect(db) as conn:
        conn.execute("BEGIN IMMEDIATE")
        logbook.append(conn, robot, operation, "accepted")
        conn.rollback()
    assert logbook.batch(db, robot) == []
    monkeypatch.setattr(logbook, "MAX_EVENTS", 2)
    first = append(db, robot, operation)
    second = append(db, robot, operation, "running")
    assert logbook.batch(db, robot) == [first, second]
    with sqlite3.connect(db) as conn:
        conn.execute("BEGIN IMMEDIATE")
        with pytest.raises(logbook.OutboxFull):
            logbook.append(conn, robot, operation, "succeeded")
    assert logbook.batch(db, robot) == [first, second]
    with pytest.raises(logbook.OutboxError):
        logbook.acknowledge(db, robot, [first, second], 3)
    assert logbook.batch(db, robot) == [first, second]
    logbook.acknowledge(db, robot, [first, second], 2)
    assert logbook.batch(db, robot) == []
    third = append(db, robot, operation, "succeeded")
    assert third["local_sequence"] == 3
    with sqlite3.connect(db) as conn:
        conn.execute("BEGIN IMMEDIATE")
        with pytest.raises(sqlite3.IntegrityError):
            logbook.append(conn, robot, operation, "running", event_id=third["event_id"])
        fourth = logbook.append(conn, robot, operation, "running")
    assert fourth["local_sequence"] == 4
    with pytest.raises(logbook.OutboxError):
        append(db, identity(), operation)


def test_batch_limits_and_gap_detection(tmp_path, monkeypatch):
    db = setup(tmp_path)
    robot, operation = identity(), identity()
    events = [append(db, robot, operation) for _ in range(3)]
    monkeypatch.setattr(logbook, "MAX_BATCH_EVENTS", 2)
    assert logbook.batch(db, robot) == events[:2]
    monkeypatch.setattr(logbook, "MAX_BATCH_BYTES", len(json.dumps({"events": events[:1]}, separators=(",", ":")).encode()))
    assert logbook.batch(db, robot) == events[:1]
    with sqlite3.connect(db) as conn:
        conn.execute("DELETE FROM logbook_outbox WHERE local_sequence=1")
    with pytest.raises(logbook.OutboxError, match="gap"):
        logbook.batch(db, robot)


def test_delivery_replays_after_lost_response_and_validates_ack(tmp_path):
    db = setup(tmp_path)
    robot, operation = identity(), identity()
    secret_path = tmp_path / "secret"
    secret_path.write_text("test-secret")
    with sqlite3.connect(db) as conn:
        conn.execute("INSERT INTO device_identity VALUES (1,?,?,?,?)", (robot, identity(), "http://platform", identity()))
    event = append(db, robot, operation)
    attempts = []
    def respond(request):
        attempts.append(json.loads(request.content))
        if len(attempts) == 1:
            raise httpx.ReadTimeout("lost response")
        if len(attempts) == 2:
            return httpx.Response(200, json={"acknowledged_sequence": True})
        return httpx.Response(200, json={"acknowledged_sequence": 1})
    settings = Settings(db_path=db, runner_url="http://runner", credential_path=str(secret_path))
    async def run():
        async with httpx.AsyncClient(transport=httpx.MockTransport(respond)) as client:
            with pytest.raises(httpx.ReadTimeout):
                await logbook.deliver_once(settings, client)
            with pytest.raises(logbook.OutboxError):
                await logbook.deliver_once(settings, client)
            assert logbook.batch(db, robot) == [event]
            assert await logbook.deliver_once(settings, client)
    asyncio.run(run())
    assert attempts == [{"events": [event]}] * 3
    assert logbook.batch(db, robot) == []


def test_author_migration_preserves_existing_outbox(tmp_path):
    from ringfour_daemon.db import MIGRATIONS_DIR

    db = str(tmp_path / "previous.db")
    robot, operation = identity(), identity()
    with sqlite3.connect(db) as conn:
        conn.execute("CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)")
        for path in sorted(MIGRATIONS_DIR.glob("*.sql")):
            version = int(path.name.split("_")[0])
            if version > 9:
                break
            conn.executescript(path.read_text())
            conn.execute("INSERT INTO schema_migrations VALUES (?, '2026-01-01')", (version,))
    event = append(db, robot, operation)
    assert migrate(db) == [10, 11]
    assert logbook.batch(db, robot) == [event]
