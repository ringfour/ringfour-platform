import sqlite3
from datetime import UTC, datetime, timedelta

import pytest
from ringfour_daemon import operations
from ringfour_daemon.db import migrate

COMMAND = {
    "operation_id": "4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b",
    "kind": "diagnostic.noop",
    "payload_version": 1,
    "payload": {"flag": True},
    "deadline_at": "2026-11-10T09:01:05Z",
}

ROBOT_ID = "7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e"


@pytest.fixture
def db_path(tmp_path):
    path = str(tmp_path / "daemon.db")
    migrate(path)
    return path


def test_migration_creates_device_operations(db_path):
    with sqlite3.connect(db_path) as conn:
        columns = {
            row[1] for row in conn.execute("PRAGMA table_info(device_operations)")
        }
        assert {
            "operation_id",
            "robot_id",
            "kind",
            "payload_version",
            "payload",
            "state",
            "deadline_at",
            "updated_at",
            "result",
        } <= columns


def test_record_command_commits_accepted_row(db_path):
    operation, created = operations.record_command(db_path, ROBOT_ID, COMMAND)

    assert created
    assert operation.state == "accepted"
    stored = operations.get(db_path, COMMAND["operation_id"])
    assert stored is not None
    assert stored.kind == "diagnostic.noop"
    assert stored.payload == {"flag": True}
    assert operations.get(db_path, stored.operation_id) == stored


def test_identical_command_replays_without_duplicate(db_path):
    operations.record_command(db_path, ROBOT_ID, COMMAND)

    replayed, created = operations.record_command(db_path, ROBOT_ID, dict(COMMAND))

    assert not created
    assert replayed.state == "accepted"
    with sqlite3.connect(db_path) as conn:
        count = conn.execute("SELECT count(*) FROM device_operations").fetchone()[0]
    assert count == 1


def test_conflicting_reuse_of_operation_id_is_an_error(db_path):
    operations.record_command(db_path, ROBOT_ID, COMMAND)
    conflicting = dict(COMMAND, payload={"flag": False})

    with pytest.raises(operations.OperationConflict):
        operations.record_command(db_path, ROBOT_ID, conflicting)


def test_replay_accepts_equivalent_postgres_timestamp_precision(db_path):
    original = dict(COMMAND, deadline_at="2026-11-10T09:01:05.1234567Z")
    replay = dict(COMMAND, deadline_at="2026-11-10T09:01:05.123456Z")
    operations.record_command(db_path, ROBOT_ID, original)
    operation, created = operations.record_command(db_path, ROBOT_ID, replay)
    assert not created
    assert operation.operation_id == COMMAND["operation_id"]


def test_command_for_another_robot_is_an_error(db_path):
    operations.record_command(db_path, ROBOT_ID, COMMAND)

    with pytest.raises(operations.OperationConflict):
        operations.record_command(
            db_path, "00000000-0000-0000-0000-000000000000", dict(COMMAND)
        )


def test_deadline_expiry_uses_the_stored_deadline():
    now = datetime.now(UTC)
    expired = (now - timedelta(seconds=1)).isoformat().replace("+00:00", "Z")
    pending = (now + timedelta(seconds=1)).isoformat().replace("+00:00", "Z")

    assert operations.is_expired(expired, now)
    assert not operations.is_expired(pending, now)
