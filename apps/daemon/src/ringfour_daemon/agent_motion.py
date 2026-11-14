"""Optional, bounded Agent journal handoff. Does not yet deliver to Platform."""
import asyncio
import json
import logging
import re
import sqlite3
import uuid
from contextlib import closing
from datetime import datetime

from . import logbook
from .enrollment import load_identity

logger = logging.getLogger(__name__)
MAX_EVENTS = 4096
MAX_RESPONSE = 256 * 1024
_TIMESTAMP = re.compile(r'^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,6})?Z$')
_EVENT_TYPES = {'motion.accepted', 'motion.refused', 'motion.completed', 'motion.cancelled',
                'motion.failed', 'motion.stop_verified', 'motion.guard_tripped', 'motion.history_incomplete'}


class HandoffError(ValueError):
    pass


def _id(value):
    try:
        return isinstance(value, str) and str(uuid.UUID(value)) == value
    except ValueError:
        return False


def _checkpoint(path):
    with closing(sqlite3.connect(path)) as conn:
        row = conn.execute('SELECT stream_id,sequence FROM agent_motion_checkpoint WHERE singleton_id=1').fetchone()
    return row or (None, 0)


async def _request(path, message):
    writer = None
    try:
        reader, writer = await asyncio.wait_for(asyncio.open_unix_connection(path, limit=MAX_RESPONSE), 5)
        writer.write(json.dumps(message, separators=(',', ':')).encode() + b'\n')
        await asyncio.wait_for(writer.drain(), 5)
        raw = await asyncio.wait_for(reader.readline(), 5)
        if not raw.endswith(b'\n') or len(raw) > MAX_RESPONSE:
            raise HandoffError('Invalid Agent response')
        response = json.loads(raw)
        if (isinstance(response, dict) and response.get('ok') is False and set(response) == {'ok', 'error'}
                and response['error'] in ('history_gap', 'invalid_request', 'journal_unavailable')):
            raise HandoffError(f"Agent handoff: {response['error']}")
        if not isinstance(response, dict) or response.get('ok') is not True or set(response) != {'ok', 'result'}:
            raise HandoffError('Agent refused the handoff')
        return response['result']
    except HandoffError:
        raise
    except (OSError, TimeoutError, ValueError) as exc:
        raise HandoffError('Agent journal handoff failed') from exc
    finally:
        if writer:
            writer.close()
            try:
                await writer.wait_closed()
            except OSError:
                pass


def _store(path, old_stream, after, page):
    if not isinstance(page, dict) or set(page) != {'stream_id', 'acknowledged_sequence', 'last_sequence', 'events'}:
        raise HandoffError('Invalid Agent page')
    stream, ack, last, events = (page['stream_id'], page['acknowledged_sequence'], page['last_sequence'], page['events'])
    if (not _id(stream) or (old_stream is not None and old_stream != stream)
            or type(ack) is not int or type(last) is not int or not 0 <= ack <= after <= last <= 9007199254740991
            or not isinstance(events, list) or len(events) > 100):
        raise HandoffError('Agent stream changed or cursor is invalid')
    encoded = []
    for sequence, event in enumerate(events, after + 1):
        if (not isinstance(event, dict) or set(event) != {'event_id', 'stream_id', 'sequence', 'occurred_at', 'event_type', 'operation_id', 'session_id', 'owner', 'stop_verified'}
                or not _id(event['event_id']) or event['stream_id'] != stream or type(event['sequence']) is not int
                or event['sequence'] != sequence or sequence > last):
            raise HandoffError('Invalid Agent event')
        if (not isinstance(event['occurred_at'], str) or not _TIMESTAMP.fullmatch(event['occurred_at'])
                or not isinstance(event['event_type'], str) or event['event_type'] not in _EVENT_TYPES
                or (event['stop_verified'] is not True if event['event_type'] == 'motion.stop_verified' else event['stop_verified'] is not None)
                or any(value is not None and (not isinstance(value, str) or '\x00' in value or not 1 <= len(value) <= 200)
                       for value in (event['operation_id'], event['session_id'], event['owner']))):
            raise HandoffError('Invalid Agent event content')
        try:
            datetime.fromisoformat(event['occurred_at'])
        except ValueError as exc:
            raise HandoffError('Invalid Agent event timestamp') from exc
        body = json.dumps(event, ensure_ascii=False, separators=(',', ':'))
        if len(body.encode()) > 8192:
            raise HandoffError('Agent event too large')
        encoded.append((sequence, event['event_id'], body))
    if not events and after < last:
        raise HandoffError('Agent history has a gap')
    with closing(sqlite3.connect(path)) as conn, conn:
        conn.execute('BEGIN IMMEDIATE')
        current = conn.execute('SELECT stream_id,sequence FROM agent_motion_checkpoint WHERE singleton_id=1').fetchone()
        if (current or (None, 0)) != (old_stream, after):
            raise HandoffError('Agent checkpoint changed concurrently')
        count = conn.execute('SELECT count(*) FROM agent_motion_inbox').fetchone()[0]
        if count + len(encoded) > MAX_EVENTS:
            raise HandoffError('Agent motion inbox is full')
        conn.executemany('INSERT INTO agent_motion_inbox VALUES (?,?,?)', encoded)
        sequence = after + len(encoded)
        conn.execute('INSERT INTO agent_motion_checkpoint VALUES (1,?,?) ON CONFLICT(singleton_id) DO UPDATE SET sequence=excluded.sequence', (stream, sequence))
    return stream, sequence


async def sync_once(db_path, socket_path):
    stream, after = await asyncio.to_thread(_checkpoint, db_path)
    page = await _request(socket_path, {'method': 'read', 'stream_id': stream, 'after': after, 'limit': 100})
    stream, sequence = await asyncio.to_thread(_store, db_path, stream, after, page)
    await _request(socket_path, {'method': 'acknowledge', 'stream_id': stream, 'sequence': sequence})
    return len(page['events'])


def promote(db_path, robot_id):
    """Atomically allocate a device sequence and move one event to the outbox."""
    with closing(sqlite3.connect(db_path)) as conn, conn:
        conn.execute('BEGIN IMMEDIATE')
        row = conn.execute('SELECT sequence,event_json FROM agent_motion_inbox ORDER BY sequence LIMIT 1').fetchone()
        if row is None:
            return False
        sequence, encoded = row
        event = json.loads(encoded)
        logbook.append(conn, robot_id, None, None, event_id=event['event_id'],
                       now=datetime.fromisoformat(event['occurred_at']), motion_payload=event)
        conn.execute('DELETE FROM agent_motion_inbox WHERE sequence=?', (sequence,))
    return True


async def run(settings):
    if not settings.agent_motion_socket:
        return
    while True:
        try:
            identity = await asyncio.to_thread(load_identity, settings.db_path)
            if identity is not None:
                while await asyncio.to_thread(promote, settings.db_path, identity['robot_id']):
                    pass
            await sync_once(settings.db_path, settings.agent_motion_socket)
        except (HandoffError, logbook.OutboxError, sqlite3.Error) as exc:
            logger.warning('Agent motion handoff failed; retained events will be retried: %s', exc)
        await asyncio.sleep(2)
