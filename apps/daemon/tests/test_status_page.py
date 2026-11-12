import json
from datetime import UTC, datetime, timedelta

from fastapi.testclient import TestClient

from ringfour_daemon.config import Settings
from ringfour_daemon.db import migrate
from ringfour_daemon.enrollment import save_identity
from ringfour_daemon.pairing import create_pending, pending_path
from ringfour_daemon.status_page import create_status_app


def test_page_shows_code_then_identity_without_exposing_control_api(tmp_path):
    settings = Settings(db_path=str(tmp_path / "daemon.db"), runner_url="http://runner",
                        platform_url="http://platform", credential_path=str(tmp_path / "secret"))
    migrate(settings.db_path)
    client = TestClient(create_status_app(settings))
    assert "Waiting for platform" in client.get("/").text
    pending = create_pending(settings)
    pending["expires_at"] = (datetime.now(UTC) + timedelta(minutes=10)).isoformat()
    pending_path(settings).write_text(json.dumps(pending))
    response = client.get("/")
    assert "Ready to pair" in response.text and "Ringfour Console" in response.text
    assert pending["secret"] not in response.text
    assert response.headers["cache-control"] == "no-store"
    assert client.post("/v1/enroll", json={"token": "anything"}).status_code == 404
    assert client.get("/fonts/SpaceGrotesk-Medium.woff2").status_code == 200
    assert save_identity(settings, {"robot_id": "7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e",
                                   "organization_id": "org", "platform_url": "http://platform",
                                   "credential_id": "credential"}, pending["secret"])
    enrolled = client.get("/").text
    assert "Enrolled · reconnecting" in enrolled
    assert "Ready to pair" not in enrolled and pending["secret"] not in enrolled
