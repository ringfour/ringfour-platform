import asyncio
import json
import sqlite3
from datetime import UTC, datetime, timedelta
from types import SimpleNamespace

import pytest
from ringfour_daemon import runtime_interactions
from ringfour_daemon.db import migrate
from ringfour_daemon.runtime_interactions import _validate


def interaction_body(*, deadline=None, timeout_outcome=None):
    return json.dumps({
        "request_id": "8ad8de90-65d7-43f5-b2be-8d0639b62e32",
        "prompt": "Continue the inspection?",
        "choices": [
            {"value": "continue", "label": "Continue"},
            {"value": "abort", "label": "Abort safely"},
        ],
        "deadline": deadline,
        "timeout_outcome": timeout_outcome,
    }).encode()


def test_interaction_without_deadline_is_valid_and_keeps_no_timeout():
    value, deadline, _ = _validate(interaction_body())
    assert deadline is None
    assert value["deadline"] is None
    assert value["timeout_outcome"] is None


def test_interaction_timeout_configuration_requires_both_fields():
    with pytest.raises(ValueError, match="provided together"):
        _validate(interaction_body(timeout_outcome="abort"))
    with pytest.raises(ValueError, match="provided together"):
        _validate(interaction_body(
            deadline=(datetime.now(UTC) + timedelta(minutes=1)).isoformat().replace("+00:00", "Z")
        ))


def test_migration_allows_no_deadline_and_preserves_existing_deadlines(tmp_path):
    db_path = str(tmp_path / "daemon.db")
    migrate(db_path)
    with sqlite3.connect(db_path) as conn:
        conn.execute(
            "INSERT INTO runtime_interactions VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)",
            ("no-timeout", "execution-a", "robot-a", "hash-a", "wait", "[]", None,
             None, "pending", None, None, 1.0, 1.0),
        )
        conn.execute(
            "INSERT INTO runtime_interactions VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)",
            ("with-timeout", "execution-b", "robot-a", "hash-b", "wait", "[]", 500.0,
             "abort", "pending", None, None, 1.0, 1.0),
        )
        rows = conn.execute(
            "SELECT request_id, deadline, timeout_outcome FROM runtime_interactions ORDER BY request_id"
        ).fetchall()
    assert rows == [("no-timeout", None, None), ("with-timeout", 500.0, "abort")]


def test_reconcile_leaves_interaction_without_deadline_pending(tmp_path, monkeypatch):
    db_path = str(tmp_path / "daemon.db")
    migrate(db_path)
    with sqlite3.connect(db_path) as conn:
        conn.execute(
            "INSERT INTO runtime_interactions VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)",
            ("no-timeout", "execution-a", "robot-a", "hash-a", "wait", "[]", None,
             None, "pending", None, None, 1.0, 1.0),
        )

    async def still_running(_settings, _execution):
        return True

    async def stop_after_reconcile(_seconds):
        raise asyncio.CancelledError

    monkeypatch.setattr(runtime_interactions, "_running", still_running)
    monkeypatch.setattr(runtime_interactions, "asyncio", SimpleNamespace(sleep=stop_after_reconcile))
    with pytest.raises(asyncio.CancelledError):
        asyncio.run(runtime_interactions.reconcile(SimpleNamespace(db_path=db_path)))

    with sqlite3.connect(db_path) as conn:
        assert conn.execute("SELECT state, outcome FROM runtime_interactions").fetchone() == ("pending", None)
