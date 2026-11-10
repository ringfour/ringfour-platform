import os
import stat

import httpx
from fastapi.testclient import TestClient

from ringfour_daemon.config import Settings
from ringfour_daemon.enrollment import load_identity
from ringfour_daemon.main import create_app

ENROLL_RESPONSE = {
    "robot_id": "7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e",
    "organization_id": "9b8a7c6d-5e4f-4a3b-8c7d-6e5f4a3b2c1d",
    "credential_id": "3c4d5e6f-7a8b-4c9d-0e1f-2a3b4c5d6e7f",
    "credential_secret": "rfs_X4pQvR7sT1wYzA3bC6dE8fG0hI1jK4lM5nO6p9N2dKq",
}


def make_client(tmp_path, platform_handler) -> TestClient:
    settings = Settings(
        db_path=str(tmp_path / "daemon.db"),
        runner_url="http://runner",
        platform_url="http://platform",
        credential_path=str(tmp_path / "secret" / "robot-credential"),
    )

    def factory() -> httpx.Client:
        return httpx.Client(transport=httpx.MockTransport(platform_handler))

    return TestClient(create_app(settings, http_client_factory=factory))


def platform_enrolls(request: httpx.Request) -> httpx.Response:
    assert request.url.path == "/v1/device/enroll"
    return httpx.Response(200, json=ENROLL_RESPONSE)


def test_enroll_success_persists_identity_and_protected_secret(tmp_path):
    client = make_client(tmp_path, platform_enrolls)
    response = client.post("/v1/enroll", json={"token": "rft_valid"})
    assert response.status_code == 200
    assert response.json() == {
        "robot_id": ENROLL_RESPONSE["robot_id"],
        "organization_id": ENROLL_RESPONSE["organization_id"],
        "credential_id": ENROLL_RESPONSE["credential_id"],
    }

    # The binding never contains the secret; the secret file is owner-only.
    db_path = str(tmp_path / "daemon.db")
    identity = load_identity(db_path)
    assert identity is not None
    assert identity["robot_id"] == ENROLL_RESPONSE["robot_id"]
    assert identity["platform_url"] == "http://platform"
    assert "secret" not in identity
    credential_path = tmp_path / "secret" / "robot-credential"
    assert credential_path.read_text() == ENROLL_RESPONSE["credential_secret"]
    mode = stat.S_IMODE(os.stat(credential_path).st_mode)
    assert mode == 0o600

    identity_view = client.get("/v1/identity")
    assert identity_view.status_code == 200
    assert identity_view.json() == {
        "enrolled": True,
        "robot_id": ENROLL_RESPONSE["robot_id"],
        "organization_id": ENROLL_RESPONSE["organization_id"],
        "platform_url": "http://platform",
        "credential_id": ENROLL_RESPONSE["credential_id"],
        "error": None,
    }


def test_enrollment_survives_restart(tmp_path):
    first = make_client(tmp_path, platform_enrolls)
    assert first.post("/v1/enroll", json={"token": "rft_valid"}).status_code == 200

    # A fresh app over the same database simulates a daemon restart.
    restarted = make_client(tmp_path, platform_enrolls)
    view = restarted.get("/v1/identity").json()
    assert view["enrolled"] is True
    assert view["robot_id"] == ENROLL_RESPONSE["robot_id"]
    assert view["error"] is None


def test_double_enroll_is_rejected(tmp_path):
    client = make_client(tmp_path, platform_enrolls)
    assert client.post("/v1/enroll", json={"token": "rft_valid"}).status_code == 200
    second = client.post("/v1/enroll", json={"token": "rft_another"})
    assert second.status_code == 409


def test_rejected_token_persists_nothing(tmp_path):
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            401,
            json={"error": {"code": "invalid_token", "message": "no", "request_id": "x"}},
        )

    client = make_client(tmp_path, handler)
    response = client.post("/v1/enroll", json={"token": "rft_bad"})
    assert response.status_code == 401
    assert load_identity(str(tmp_path / "daemon.db")) is None
    assert not (tmp_path / "secret" / "robot-credential").exists()
    assert client.get("/v1/identity").json() == {"enrolled": False, "error": None}


def test_unreachable_platform_persists_nothing(tmp_path):
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("connection refused", request=request)

    client = make_client(tmp_path, handler)
    response = client.post("/v1/enroll", json={"token": "rft_valid"})
    assert response.status_code == 502
    assert load_identity(str(tmp_path / "daemon.db")) is None


def test_missing_credential_is_reported_after_restart(tmp_path):
    client = make_client(tmp_path, platform_enrolls)
    assert client.post("/v1/enroll", json={"token": "rft_valid"}).status_code == 200
    os.unlink(tmp_path / "secret" / "robot-credential")

    restarted = make_client(tmp_path, platform_enrolls)
    view = restarted.get("/v1/identity").json()
    assert view["enrolled"] is True
    assert view["error"] == "credential_missing"
