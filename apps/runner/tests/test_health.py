import shutil

import pytest
from fastapi.testclient import TestClient

from ringfour_runner.config import Settings
from ringfour_runner.main import create_app


def make_client(data_dir: str) -> TestClient:
    return TestClient(create_app(Settings(data_dir=data_dir)))


def test_liveness_reports_ok(tmp_path):
    client = make_client(str(tmp_path))
    response = client.get("/health/live")
    assert response.status_code == 200
    assert response.json() == {"status": "ok", "version": "0.1.0"}


def test_readiness_ok_with_writable_volume_and_uv(tmp_path):
    if shutil.which("uv") is None:
        pytest.skip("uv not installed on this host")
    client = make_client(str(tmp_path))
    response = client.get("/health/ready")
    assert response.status_code == 200
    assert response.json() == {
        "status": "ok",
        "version": "0.1.0",
        "checks": {"volume": "ok", "uv": "ok"},
    }


def test_readiness_fails_when_volume_unwritable(tmp_path):
    # A regular file as data dir makes every write fail with NotADirectoryError.
    blocker = tmp_path / "blocker"
    blocker.write_text("not a directory")
    client = make_client(str(blocker))
    response = client.get("/health/ready")
    assert response.status_code == 503
    body = response.json()
    assert body["status"] == "fail"
    assert body["checks"]["volume"] == "fail"
