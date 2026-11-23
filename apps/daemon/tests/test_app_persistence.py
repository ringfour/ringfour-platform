"""Database constraints must survive legacy replay and reopened connections."""

import sqlite3
import uuid

import pytest
from ringfour_daemon import scripts
from ringfour_daemon.db import MIGRATIONS_DIR, connect, migrate

NOW = "2026-11-23T12:00:00Z"


def identity(conn):
    conn.execute("INSERT INTO device_identity VALUES (1, 'robot', 'org', 'http://platform', 'credential')")


def operation(conn, operation_id):
    conn.execute(
        "INSERT INTO device_operations VALUES (?, 'robot', 'script.execution.start', 1, '{}', 'accepted', ?, ?, NULL)",
        (operation_id, NOW, NOW),
    )


def test_apps_upgrade_retains_identity_active_script_and_namespace_on_reopen(tmp_path):
    db = str(tmp_path / "daemon.db")
    with sqlite3.connect(db) as conn:
        conn.execute("CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)")
        for path in sorted(MIGRATIONS_DIR.glob("*.sql")):
            version = int(path.name.split("_")[0])
            if version >= 15:
                break
            conn.executescript(path.read_text())
            conn.execute("INSERT INTO schema_migrations VALUES (?, ?)", (version, NOW))
        identity(conn)
        operation(conn, "legacy")
        conn.execute("INSERT INTO script_sync(execution_id, started) VALUES ('legacy', 1)")
    assert migrate(db) == [15, 16]
    with connect(db) as conn:
        namespace = conn.execute("SELECT deployment_namespace FROM device_deployment_namespace").fetchone()[0]
        assert str(uuid.UUID(namespace)) == namespace
        assert conn.execute("SELECT source, execution_id FROM device_execution_slots").fetchone() == ("legacy", "legacy")
    assert migrate(db) == []
    with connect(db) as conn:
        assert conn.execute("SELECT deployment_namespace FROM device_deployment_namespace").fetchone() == (namespace,)
        assert conn.execute("SELECT robot_id, organization_id, credential_id FROM device_identity").fetchone() == ("robot", "org", "credential")
        assert conn.execute("SELECT started, completed FROM script_sync").fetchone() == (1, 0)


def test_legacy_ignore_conflicts_cannot_bypass_shared_slot(tmp_path):
    db = str(tmp_path / "daemon.db")
    migrate(db)
    with connect(db) as conn:
        identity(conn)
        operation(conn, "legacy")
        operation(conn, "app-operation")
        conn.execute("INSERT INTO app_installations(installation_id, robot_id, app_id, updated_at) VALUES ('installation', 'robot', 'app', ?)", (NOW,))

    def app_start():
        with connect(db) as conn:
            conn.execute(
                "INSERT OR IGNORE INTO app_runs(run_id, installation_id, robot_id, operation_id, app_id, revision_id, image_digest, configuration_revision_id, snapshot_json, state, requested_at, updated_at) "
                "VALUES ('run', 'installation', 'robot', 'app-operation', 'app', 'revision', ?, 'configuration', '{}', 'accepted', ?, ?)",
                ("sha256:" + "a" * 64, NOW, NOW),
            )

    scripts.record_sync(db, "legacy")
    scripts.record_sync(db, "legacy")  # Identical replay is still permitted.
    with pytest.raises(sqlite3.IntegrityError, match="execution_conflict"):
        app_start()
    scripts.complete_sync(db, "legacy")
    app_start()
    with connect(db) as conn:
        operation(conn, "second-legacy")
    with pytest.raises(sqlite3.IntegrityError, match="execution_conflict"):
        scripts.record_sync(db, "second-legacy")
    with connect(db) as conn:
        assert conn.execute("SELECT count(*) FROM script_sync WHERE execution_id='second-legacy'").fetchone() == (0,)
        conn.execute("UPDATE app_runs SET state='failed' WHERE run_id='run'")
    scripts.record_sync(db, "second-legacy")
    with connect(db) as conn:
        assert conn.execute("PRAGMA foreign_keys").fetchone() == (1,)
        assert conn.execute("SELECT source, execution_id FROM device_execution_slots").fetchone() == ("legacy", "second-legacy")
