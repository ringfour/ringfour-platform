
import httpx
import pytest
from fastapi.testclient import TestClient
from ringfour_daemon.config import Settings
from ringfour_daemon.db import migrate
from ringfour_daemon.main import create_app


def make_client(tmp_path, runner_handler) -> TestClient:
    settings = Settings(db_path=str(tmp_path / "daemon.db"), )

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


def test_startup_fails_when_sqlite_directory_is_unusable(tmp_path):
    # A regular file as the database parent directory makes migrations fail fast.
    blocker = tmp_path / "blocker"
    blocker.write_text("not a directory")
    settings = Settings(db_path=str(blocker / "daemon.db"), )
    with pytest.raises(OSError):
        create_app(settings)


def test_migrations_are_repeatable(tmp_path):
    db = str(tmp_path / "daemon.db")
    first = migrate(db)
    second = migrate(db)
    assert first == [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23]
    assert second == []


def test_app_readiness_uses_engine_without_calling_runner(tmp_path, monkeypatch):
    from ringfour_daemon import app_engine
    class Engine:
        def __init__(self, settings): pass
        def platform(self): return 'linux/amd64'
        def close(self): pass
    monkeypatch.setattr(app_engine, 'Engine', Engine)
    settings = Settings(db_path=str(tmp_path/'app.db'),apps_enabled=True)
    def forbidden(): raise AssertionError('App readiness called Runner')
    client = TestClient(create_app(settings,http_client_factory=forbidden))
    assert client.get('/health/ready').json()['checks']=={'sqlite':'ok','engine':'ok'}


def test_app_readiness_missing_socket_is_distinct(tmp_path):
    settings = Settings(db_path=str(tmp_path/'app.db'),apps_enabled=True,engine_socket=str(tmp_path/'missing.sock'))
    client=TestClient(create_app(settings))
    response=client.get('/health/ready')
    assert response.status_code==503 and response.json()['checks']=={'sqlite':'ok','engine':'fail'}
