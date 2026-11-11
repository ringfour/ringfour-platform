import json
import sqlite3
from dataclasses import dataclass
from datetime import UTC, datetime

# Local deduplication and reconciliation records for hosted operations. The
# platform owns queued; a device record starts at accepted once the command is
# committed locally (before the ack goes out). Terminal states are immutable.
TERMINAL_STATES = ("succeeded", "failed", "cancelled")


class OperationConflict(Exception):
    """An operation id was reused with different command content."""


class OperationNotFound(Exception):
    pass


@dataclass(frozen=True)
class DeviceOperation:
    operation_id: str
    robot_id: str
    kind: str
    payload_version: int
    payload: dict
    state: str
    deadline_at: str
    updated_at: str
    result: dict | None


def utc_now() -> str:
    return datetime.now(UTC).isoformat(timespec="microseconds")


def canonical(payload: dict) -> str:
    return json.dumps(payload, sort_keys=True, separators=(",", ":"))


def is_expired(deadline_at: str, now: datetime) -> bool:
    return parse_ts(deadline_at) <= now


def parse_ts(value: str) -> datetime:
    return datetime.fromisoformat(value)


def _row_to_operation(row: sqlite3.Row) -> DeviceOperation:
    return DeviceOperation(
        operation_id=row["operation_id"],
        robot_id=row["robot_id"],
        kind=row["kind"],
        payload_version=row["payload_version"],
        payload=json.loads(row["payload"]),
        state=row["state"],
        deadline_at=row["deadline_at"],
        updated_at=row["updated_at"],
        result=json.loads(row["result"]) if row["result"] is not None else None,
    )


def record_command(db_path: str, robot_id: str, command: dict) -> tuple[DeviceOperation, bool]:
    """Commit the local record for a command before it is acknowledged.

    Returns (operation, created). A command whose operation id is already
    known with identical content returns the stored row with created=False;
    the caller reconciles it according to its stored state. The same id with
    different content is a protocol error.
    """
    operation_id = command["operation_id"]
    with sqlite3.connect(db_path) as conn:
        conn.row_factory = sqlite3.Row
        row = conn.execute(
            "SELECT * FROM device_operations WHERE operation_id = ?", (operation_id,)
        ).fetchone()
        if row is not None:
            stored = _row_to_operation(row)
            if (
                stored.robot_id == robot_id
                and stored.kind == command["kind"]
                and stored.payload_version == command["payload_version"]
                and stored.payload == command["payload"]
                # PostgreSQL stores microseconds while .NET can serialize a
                # seventh fractional digit before the initial insert returns.
                and parse_ts(stored.deadline_at) == parse_ts(command["deadline_at"])
            ):
                return stored, False
            raise OperationConflict(
                f"operation {operation_id} reused with conflicting content"
            )
        now = utc_now()
        conn.execute(
            "INSERT INTO device_operations"
            " (operation_id, robot_id, kind, payload_version, payload, state,"
            "  deadline_at, updated_at, result)"
            " VALUES (?, ?, ?, ?, ?, 'accepted', ?, ?, NULL)",
            (
                operation_id,
                robot_id,
                command["kind"],
                command["payload_version"],
                canonical(command["payload"]),
                command["deadline_at"],
                now,
            ),
        )
        return (
            DeviceOperation(
                operation_id=operation_id,
                robot_id=robot_id,
                kind=command["kind"],
                payload_version=command["payload_version"],
                payload=command["payload"],
                state="accepted",
                deadline_at=command["deadline_at"],
                updated_at=now,
                result=None,
            ),
            True,
        )


def apply_status(
    db_path: str, operation_id: str, state: str, result: dict | None = None
) -> DeviceOperation:
    with sqlite3.connect(db_path) as conn:
        conn.row_factory = sqlite3.Row
        row = conn.execute(
            "SELECT * FROM device_operations WHERE operation_id = ?", (operation_id,)
        ).fetchone()
        if row is None:
            raise OperationNotFound(operation_id)
        stored = _row_to_operation(row)
        if stored.state in TERMINAL_STATES:
            if stored.state == state:
                return stored
            raise OperationConflict(
                f"operation {operation_id} is already terminal ({stored.state})"
            )
        now = utc_now()
        conn.execute(
            "UPDATE device_operations SET state = ?, result = ?, updated_at = ?"
            " WHERE operation_id = ?",
            (state, canonical(result) if result is not None else None, now, operation_id),
        )
        return DeviceOperation(
            operation_id=stored.operation_id,
            robot_id=stored.robot_id,
            kind=stored.kind,
            payload_version=stored.payload_version,
            payload=stored.payload,
            state=state,
            deadline_at=stored.deadline_at,
            updated_at=now,
            result=result,
        )


def get(db_path: str, operation_id: str) -> DeviceOperation | None:
    with sqlite3.connect(db_path) as conn:
        conn.row_factory = sqlite3.Row
        row = conn.execute(
            "SELECT * FROM device_operations WHERE operation_id = ?", (operation_id,)
        ).fetchone()
        return _row_to_operation(row) if row is not None else None


def list_nonterminal(db_path: str) -> list[DeviceOperation]:
    with sqlite3.connect(db_path) as conn:
        conn.row_factory = sqlite3.Row
        rows = conn.execute(
            "SELECT * FROM device_operations"
            " WHERE state NOT IN ('succeeded', 'failed', 'cancelled')"
            " ORDER BY updated_at, operation_id"
        ).fetchall()
        return [_row_to_operation(row) for row in rows]


def mark_superseded(db_path: str, operation_id: str) -> DeviceOperation:
    """Fail a nonterminal local record the platform no longer considers active.

    Used by startup reconciliation: every nonterminal platform operation is
    replayed after welcome, so absence from the replay means terminal-or-gone.
    """
    return apply_status(
        db_path,
        operation_id,
        "failed",
        {
            "error": {
                "code": "superseded",
                "message": "The platform no longer has this operation active.",
            }
        },
    )
