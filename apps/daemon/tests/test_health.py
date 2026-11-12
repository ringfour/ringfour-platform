import sqlite3

import httpx
import pytest
from fastapi.testclient import TestClient

from ringfour_daemon.config import Settings
from ringfour_daemon.db import migrate
from ringfour_daemon.main import create_app


def make_client(tmp_path, runner_handler) -> TestClient:
    settings = Settings(db_path=str(tmp_path / "daemon.db"), runner_url="http://runner")

    def factory() -> httpx.Client:
        return httpx.Client(transport=httpx.MockTransport(runner_handler))

    return TestClient(create_app(settings, http_client_factory=factory))


def runner_ok(request: httpx.Request) -> httpx.Response:
    return httpx.Response(200, json={"status": "ok", "version": "0.1.0", "checks": {"volume": "ok", "uv": "ok"}})


def test_liveness_reports_ok_without_dependencies(tmp_path):
    client = make_client(tmp_path, runner_handler=lambda r: httpx.Response(503))
    response = client.get("/health/live")
    assert response.status_code == 200
    assert response.json() == {"status": "ok", "version": "0.1.0"}


def test_readiness_ok_when_sqlite_and_runner_healthy(tmp_path):
    client = make_client(tmp_path, runner_ok)
    response = client.get("/health/ready")
    assert response.status_code == 200
    assert response.json() == {
        "status": "ok",
        "version": "0.1.0",
        "checks": {"sqlite": "ok", "runner": "ok"},
    }


def test_readiness_fails_when_runner_unhealthy(tmp_path):
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(503, json={"status": "fail", "version": "0.1.0", "checks": {"volume": "fail", "uv": "ok"}})

    client = make_client(tmp_path, handler)
    response = client.get("/health/ready")
    assert response.status_code == 503
    body = response.json()
    assert body["status"] == "fail"
    assert body["checks"] == {"sqlite": "ok", "runner": "fail"}


def test_readiness_fails_when_runner_unreachable(tmp_path):
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("connection refused", request=request)

    client = make_client(tmp_path, handler)
    response = client.get("/health/ready")
    assert response.status_code == 503
    assert response.json()["checks"]["runner"] == "fail"


def test_startup_fails_when_sqlite_directory_is_unusable(tmp_path):
    # A regular file as the database parent directory makes migrations fail fast.
    blocker = tmp_path / "blocker"
    blocker.write_text("not a directory")
    settings = Settings(db_path=str(blocker / "daemon.db"), runner_url="http://runner")
    with pytest.raises(OSError):
        create_app(settings)


def test_migrations_are_repeatable(tmp_path):
    db = str(tmp_path / "daemon.db")
    first = migrate(db)
    second = migrate(db)
    assert first == [1, 2, 3, 4, 5]
    assert second == []


def test_script_sync_migrates_from_previous_schema_with_identity_and_operations(tmp_path):
    from ringfour_daemon.db import MIGRATIONS_DIR

    db = str(tmp_path / "old-daemon.db")
    with sqlite3.connect(db) as conn:
        conn.execute("CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)")
        for path in sorted(MIGRATIONS_DIR.glob("000[123]_*.sql")):
            version = int(path.name.split("_")[0])
            conn.executescript(path.read_text())
            conn.execute("INSERT INTO schema_migrations VALUES (?, '2026-01-01T00:00:00Z')", (version,))
        conn.execute("INSERT INTO device_identity VALUES (1, 'robot', 'org', 'https://api.example', 'credential')")
        conn.execute("INSERT INTO device_operations VALUES ('operation', 'robot', 'diagnostic.noop', 1, '{}', 'succeeded', '2026-01-01', '2026-01-01', '{}')")
    assert migrate(db) == [4, 5]
    with sqlite3.connect(db) as conn:
        assert conn.execute("SELECT robot_id FROM device_identity").fetchone() == ("robot",)
        assert conn.execute("SELECT operation_id FROM device_operations").fetchone() == ("operation",)
        assert conn.execute("SELECT count(*) FROM script_sync").fetchone() == (0,)
