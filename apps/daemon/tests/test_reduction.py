"""Retirement removes old state and routes while preserving App identity/state."""
import sqlite3
import uuid

from fastapi.testclient import TestClient
from ringfour_daemon.config import Settings
from ringfour_daemon.db import MIGRATIONS_DIR, connect, migrate
from ringfour_daemon.main import create_app


def test_upgrade_discards_legacy_state_and_preserves_enrollment_and_app_binding(tmp_path):
    db = str(tmp_path / "upgrade.db")
    with sqlite3.connect(db) as conn:
        conn.execute("CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)")
        for path in sorted(MIGRATIONS_DIR.glob("*.sql")):
            version = int(path.name.split("_")[0])
            if version >= 22:
                break
            conn.executescript(path.read_text())
            conn.execute("INSERT INTO schema_migrations VALUES (?, '2026-11-25')", (version,))
        conn.execute("INSERT INTO device_identity VALUES (1,'robot','org','http://platform','credential')")
        namespace = str(uuid.uuid4())
        conn.execute("INSERT INTO device_deployment_namespace VALUES (1,?,'2026-11-25')", (namespace,))
        conn.execute("INSERT INTO app_installations(installation_id,robot_id,app_id,updated_at) VALUES ('installed','robot','app','2026-11-25')")
        conn.execute("INSERT INTO device_operations VALUES ('legacy','robot','script.execution.start',1,'{}','accepted','2026-11-25','2026-11-25',NULL)")
        conn.execute("INSERT INTO script_sync(execution_id) VALUES ('legacy')")
    assert migrate(db) == [22]
    assert migrate(db) == []
    with connect(db) as conn:
        tables = {r[0] for r in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        assert not tables.intersection({'script_sync', 'local_dev_sessions', 'local_dev_runs', 'local_source_syncs', 'live_artifact_queue'})
        assert conn.execute("SELECT robot_id FROM device_identity").fetchone() == ('robot',)
        assert conn.execute("SELECT deployment_namespace FROM device_deployment_namespace").fetchone() == (namespace,)
        assert conn.execute("SELECT installation_id FROM app_installations").fetchone() == ('installed',)
        assert conn.execute("SELECT count(*) FROM device_execution_slots").fetchone() == (0,)
        assert conn.execute("PRAGMA foreign_key_check").fetchall() == []


def test_legacy_routes_and_execution_credentials_are_rejected(tmp_path):
    client = TestClient(create_app(Settings(db_path=str(tmp_path / 'daemon.db'))))
    assert client.get('/v1/local/dev-sessions/old').status_code == 404
    for path in ['/v1/runtime/logbook/entries', '/v1/runtime/artifacts', '/v1/runtime/interactions']:
        response = client.post(path, headers={'Authorization': 'Bearer legacy:deadbeef'}, json={})
        assert response.status_code == 401
