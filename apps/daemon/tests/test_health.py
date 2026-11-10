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
    assert first == [1, 2, 3]
    assert second == []
