import json
import sqlite3
import uuid

import pytest
from ringfour_daemon import agent_motion, logbook
from ringfour_daemon.db import migrate


def event(stream, sequence, kind='motion.accepted'):
    return {'event_id': str(uuid.uuid4()), 'stream_id': stream, 'sequence': sequence,
            'occurred_at': '2026-11-13T12:00:00Z', 'event_type': kind,
            'operation_id': 'task-1', 'session_id': None, 'owner': None,
            'stop_verified': True if kind == 'motion.stop_verified' else None}


def page(stream, events, *, ack=0, last=None):
    return {'stream_id': stream, 'acknowledged_sequence': ack,
            'last_sequence': len(events) if last is None else last, 'events': events}


def test_commit_capacity_and_gap(tmp_path, monkeypatch):
    db = str(tmp_path / 'daemon.db')
    migrate(db)
    stream = str(uuid.uuid4())
    first = event(stream, 1)
    assert agent_motion._store(db, None, 0, page(stream, [first])) == (stream, 1)
    assert agent_motion._checkpoint(db) == (stream, 1)
    monkeypatch.setattr(agent_motion, 'MAX_EVENTS', 1)
    with pytest.raises(agent_motion.HandoffError, match='full'):
        agent_motion._store(db, stream, 1, page(stream, [event(stream, 2)], last=2))
    with pytest.raises(agent_motion.HandoffError, match='stream changed'):
        agent_motion._store(db, stream, 1, page(str(uuid.uuid4()), [], last=1))
    with pytest.raises(agent_motion.HandoffError, match='gap'):
        agent_motion._store(db, stream, 1, page(stream, [], last=2))
    with sqlite3.connect(db) as conn:
        rows = conn.execute('SELECT event_json FROM agent_motion_inbox').fetchall()
    assert [json.loads(row[0]) for row in rows] == [first]
    assert agent_motion._checkpoint(db) == (stream, 1)


def test_reject_unverified_stop_without_committing(tmp_path):
    db = str(tmp_path / 'daemon.db')
    migrate(db)
    stream = str(uuid.uuid4())
    bad = event(stream, 1, 'motion.stop_verified')
    bad['stop_verified'] = False
    with pytest.raises(agent_motion.HandoffError, match='content'):
        agent_motion._store(db, None, 0, page(stream, [bad]))
    assert agent_motion._checkpoint(db) == (None, 0)


def test_promotion_is_atomic_and_uses_independent_device_sequence(tmp_path, monkeypatch):
    db = str(tmp_path / 'daemon.db')
    migrate(db)
    stream, robot = str(uuid.uuid4()), str(uuid.uuid4())
    first, second = event(stream, 1), event(stream, 2, 'motion.stop_verified')
    agent_motion._store(db, None, 0, page(stream, [first, second]))
    monkeypatch.setattr(logbook, 'MAX_EVENTS', 0)
    with pytest.raises(logbook.OutboxFull):
        agent_motion.promote(db, robot)
    with sqlite3.connect(db) as conn:
        assert conn.execute('SELECT count(*) FROM agent_motion_inbox').fetchone()[0] == 2
    monkeypatch.setattr(logbook, 'MAX_EVENTS', 10)
    assert agent_motion.promote(db, robot)
    assert agent_motion.promote(db, robot)
    assert not agent_motion.promote(db, robot)
    rows = logbook.batch(db, robot)
    assert [row['local_sequence'] for row in rows] == [1, 2]
    assert [row['payload'] for row in rows] == [first, second]
    assert all(row['operation_id'] is None and row['type'] == 'agent.motion.event' for row in rows)
    with sqlite3.connect(db) as conn:
        assert conn.execute('SELECT count(*) FROM agent_motion_inbox').fetchone()[0] == 0


def test_invalid_timestamp_is_not_acknowledged(tmp_path):
    db = str(tmp_path / 'daemon.db')
    migrate(db)
    stream = str(uuid.uuid4())
    invalid = event(stream, 1)
    invalid['occurred_at'] = '2026-99-99T12:00:00Z'
    with pytest.raises(agent_motion.HandoffError, match='timestamp'):
        agent_motion._store(db, None, 0, page(stream, [invalid]))
    assert agent_motion._checkpoint(db) == (None, 0)
