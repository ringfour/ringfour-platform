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


def test_activity_and_runner_output_are_bounded_escaped_and_read_only(tmp_path):
    import base64

    import httpx

    from ringfour_daemon import operations

    settings = Settings(db_path=str(tmp_path / "daemon.db"), runner_url="http://runner",
                        platform_url="http://platform", credential_path=str(tmp_path / "secret"),
                        runner_auth_token="runner-secret")
    migrate(settings.db_path)
    save_identity(settings, {"robot_id": "7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e",
                             "organization_id": "org", "platform_url": "http://platform",
                             "credential_id": "credential"}, "device-secret")
    for kind, entry in (("mission.prepare", "<mission>.py"), ("script.execution.start", "main.py")):
        operation_id = "7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e" if kind == "script.execution.start" else "f6e5d4c3-4b3a-4210-9876-123456789abc"
        operations.record_command(settings.db_path, "robot", {"operation_id": operation_id, "kind": kind,
            "payload_version": 1, "payload": {"entrypoint": entry, "secret": "hidden-value", **({"mission_revision_id": "revision"} if kind == "script.execution.start" else {})},
            "deadline_at": "2030-01-01T00:00:00+00:00"})
        operations.apply_status(settings.db_path, operation_id, "succeeded", {"secret": "hidden-result"})
    content = b"old" * 7000 + b"<script>danger</script> newest"
    seen = []

    def handler(request):
        seen.append(request)
        assert request.headers["authorization"] == "Bearer runner-secret"
        if not request.url.path.endswith("/output"):
            return httpx.Response(200, json={"state": "running"})
        offset = int(request.url.params["offset"])
        data = content[offset:offset + 16384] if request.url.params["stream"] == "stdout" else b""
        return httpx.Response(200, json={"offset": offset, "next_offset": offset + len(data),
            "data_base64": base64.b64encode(data).decode(), "truncated": False})

    factory = lambda **kwargs: httpx.AsyncClient(transport=httpx.MockTransport(handler), **kwargs)
    response = TestClient(create_status_app(settings, client_factory=factory)).get("/")
    text = response.text
    assert "Mission prepared" in text and "&lt;mission&gt;.py" in text and "Mission start accepted" in text
    assert "Mission active · main.py" in text and "&lt;script&gt;danger&lt;/script&gt; newest" in text
    assert "showing latest output" in text and len(text) < 14000
    assert "hidden-value" not in text and "hidden-result" not in text and "runner-secret" not in text
    assert all(request.method == "GET" for request in seen)
    assert "<script>danger</script>" not in text


def test_page_keeps_activity_when_runner_is_unreachable(tmp_path):
    import httpx

    from ringfour_daemon import operations

    settings = Settings(db_path=str(tmp_path / "daemon.db"), runner_url="http://runner",
                        platform_url="http://platform", credential_path=str(tmp_path / "secret"),
                        runner_auth_token="private")
    migrate(settings.db_path)
    save_identity(settings, {"robot_id": "robot", "organization_id": "org", "platform_url": "http://platform",
                             "credential_id": "credential"}, "secret")
    operations.record_command(settings.db_path, "robot", {"operation_id": "execution", "kind": "script.execution.start",
        "payload_version": 1, "payload": {"entrypoint": "main.py"}, "deadline_at": "2030-01-01T00:00:00+00:00"})

    def offline(request):
        raise httpx.ConnectError("unavailable", request=request)

    factory = lambda **kwargs: httpx.AsyncClient(transport=httpx.MockTransport(offline), **kwargs)
    text = TestClient(create_status_app(settings, client_factory=factory)).get("/").text
    assert "Execution received" in text and "main.py" in text and "Runner is unavailable" in text
    assert "private" not in text and "secret" not in text
