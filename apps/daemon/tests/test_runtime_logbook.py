import hashlib
import hmac
import sqlite3
import uuid

import httpx
from fastapi.testclient import TestClient

from ringfour_daemon import runtime_logbook
from ringfour_daemon.config import Settings
from ringfour_daemon.db import migrate
from ringfour_daemon.main import create_app


def test_author_entries_authenticate_running_execution_and_replay(tmp_path, monkeypatch):
    db = str(tmp_path / 'daemon.db')
    migrate(db)
    robot, execution, entry_id = (str(uuid.uuid4()) for _ in range(3))
    with sqlite3.connect(db) as conn:
        conn.execute("INSERT INTO device_identity VALUES (1,?,?,?,?)", (robot, str(uuid.uuid4()), 'http://platform', str(uuid.uuid4())))
        conn.execute("INSERT INTO device_operations VALUES (?,?,?,1,'{}','running','2099-01-01','2026-01-01',NULL)", (execution, robot, 'script.execution.start'))
    settings = Settings(db_path=db, runner_url='http://runner', runner_auth_token='runner-secret')
    app = create_app(settings)
    state = {'value': 'running'}
    def runner(request):
        assert request.headers['authorization'] == 'Bearer runner-secret'
        return httpx.Response(200, json={'execution_id': execution, 'state': state['value']})
    original = httpx.AsyncClient
    monkeypatch.setattr(runtime_logbook.httpx, 'AsyncClient', lambda **kw: original(transport=httpx.MockTransport(runner), **kw))
    mac = hmac.new(b'runner-secret', ('ringfour-logbook-v1:' + execution).encode(), hashlib.sha256).hexdigest()
    headers = {'Authorization': f'Bearer {execution}:{mac}'}
    body = {'entry_id': entry_id, 'level': 'info', 'message': 'Inspection skipped', 'fields': {'station': 'B'}}
    with TestClient(app) as client:
        path = '/v1/runtime/logbook/entries'
        assert client.post(path, json=body).status_code == 401
        assert client.post(path, headers={'Authorization': f'Bearer {execution}:wrong'}, json=body).status_code == 401
        assert client.post(path, headers=headers, json={**body, 'robot_id': robot}).status_code == 400
        assert client.post(path, headers=headers, json={**body, 'message': 'bad\x00value'}).status_code == 400
        assert client.post(path, headers=headers, json={**body, 'fields': {'bad': 1e100}}).status_code == 400
        first = client.post(path, headers=headers, json=body)
        assert first.status_code == 200 and first.json() == {'entry_id': entry_id}
        assert client.post(path, headers=headers, json=body).status_code == 200
        with sqlite3.connect(db) as conn:
            event = conn.execute('SELECT event_json FROM logbook_outbox').fetchone()[0]
            assert 'Inspection skipped' in event and 'runner-secret' not in event and mac not in event
            conn.execute('UPDATE logbook_outbox_state SET acknowledged_sequence=1')
            conn.execute('DELETE FROM logbook_outbox')
        assert client.post(path, headers=headers, json=body).status_code == 200
        assert client.post(path, headers=headers, json={**body, 'message': 'different'}).status_code == 409
        monkeypatch.setattr(runtime_logbook.logbook, 'MAX_EVENTS', 0)
        assert client.post(path, headers=headers, json={**body, 'entry_id': str(uuid.uuid4())}).status_code == 503
        state['value'] = 'succeeded'
        assert client.post(path, headers=headers, json=body).status_code == 401
    with sqlite3.connect(db) as conn:
        assert conn.execute('SELECT count(*) FROM logbook_outbox').fetchone()[0] == 0
        replay = conn.execute('SELECT request_hash FROM logbook_entry_replays').fetchone()[0]
        assert 'Inspection skipped' not in replay and 'runner-secret' not in replay and mac not in replay
