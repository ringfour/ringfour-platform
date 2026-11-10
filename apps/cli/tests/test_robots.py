import json

import httpx
import pytest
from typer.testing import CliRunner

from ringfour_cli import auth
from ringfour_cli.config import load_settings
from ringfour_cli.main import app

runner = CliRunner()

REAL_CLIENT = httpx.Client
REAL_TOKEN_STORE = auth.TokenStore

ORG_ID = "9b8a7c6d-5e4f-4a3b-8c7d-6e5f4a3b2c1d"
ROBOT_ID = "7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e"
CREDENTIAL_ID = "3c4d5e6f-7a8b-4c9d-0e1f-2a3b4c5d6e7f"
ROBOT = {
    "id": ROBOT_ID,
    "organization_id": ORG_ID,
    "name": "warehouse-01",
    "created_at": "2026-11-09T08:20:11Z",
    "enrolled_at": None,
    "profile_ref": None,
}


class FakeKeyring:
    def __init__(self):
        self.store = {}

    def set_password(self, service, account, password):
        self.store[(service, account)] = password

    def get_password(self, service, account):
        return self.store.get((service, account))

    def delete_password(self, service, account):
        del self.store[(service, account)]


@pytest.fixture()
def env(monkeypatch, tmp_path):
    monkeypatch.setenv("RINGFOUR_CONFIG", str(tmp_path / "config.json"))
    monkeypatch.setenv("RINGFOUR_AUTH0_DOMAIN", "tenant.eu.auth0.com")
    monkeypatch.setenv("RINGFOUR_AUTH0_CLIENT_ID", "cli-client-id")
    monkeypatch.setenv("RINGFOUR_API_URL", "http://api.test")
    return tmp_path


@pytest.fixture()
def keyring(monkeypatch, env):
    fake = FakeKeyring()
    monkeypatch.setattr(auth, "TokenStore", lambda: REAL_TOKEN_STORE(fake))
    store = REAL_TOKEN_STORE(fake)
    store.save(load_settings(), {"access_token": "at", "refresh_token": "rt"})
    return fake


def _client_factory(handler):
    def factory(*args, **kwargs):
        kwargs["transport"] = httpx.MockTransport(handler)
        return REAL_CLIENT(*args, **kwargs)

    return factory


def test_robot_create_posts_to_scoped_collection(monkeypatch, keyring, env):
    seen = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["method"] = request.method
        seen["url"] = str(request.url)
        seen["body"] = json.loads(request.content)
        return httpx.Response(201, json=ROBOT)

    monkeypatch.setattr(httpx, "Client", _client_factory(handler))
    result = runner.invoke(app, ["robot", "create", "warehouse-01", "--org", ORG_ID])
    assert result.exit_code == 0
    assert seen["method"] == "POST"
    assert seen["url"] == f"http://api.test/v1/organizations/{ORG_ID}/robots"
    assert seen["body"] == {"name": "warehouse-01", "profile_ref": None}
    assert ROBOT_ID in result.output


def test_robot_create_uses_selected_org(monkeypatch, keyring, env):
    (env / "config.json").write_text(json.dumps({"selected_org": ORG_ID}))
    monkeypatch.setenv("RINGFOUR_CONFIG", str(env / "config.json"))

    def handler(request: httpx.Request) -> httpx.Response:
        assert f"/v1/organizations/{ORG_ID}/robots" in str(request.url)
        return httpx.Response(201, json=ROBOT)

    monkeypatch.setattr(httpx, "Client", _client_factory(handler))
    result = runner.invoke(app, ["robot", "create", "warehouse-01"])
    assert result.exit_code == 0


def test_robot_create_without_org_fails(monkeypatch, keyring):
    result = runner.invoke(app, ["robot", "create", "warehouse-01"])
    assert result.exit_code == 1
    assert "No organization selected" in result.output


def test_robot_list_and_show(monkeypatch, keyring):
    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path.endswith("/robots"):
            return httpx.Response(200, json={"items": [ROBOT], "next_cursor": None})
        return httpx.Response(200, json=ROBOT)

    monkeypatch.setattr(httpx, "Client", _client_factory(handler))
    listing = runner.invoke(app, ["robot", "list", "--org", ORG_ID])
    assert listing.exit_code == 0
    # The table truncates long ids at the test terminal width.
    assert "7c2f9a1d" in listing.output
    assert "warehouse-01" in listing.output

    shown = runner.invoke(app, ["robot", "show", ROBOT_ID])
    assert shown.exit_code == 0
    assert "warehouse-01" in shown.output


def test_enrollment_token_is_printed_once(monkeypatch, keyring):
    def handler(request: httpx.Request) -> httpx.Response:
        assert request.method == "POST"
        assert request.url.path == f"/v1/robots/{ROBOT_ID}/enrollment-tokens"
        return httpx.Response(
            201,
            json={
                "token_id": "2b3c4d5e-6f7a-4b8c-9d0e-1f2a3b4c5d6e",
                "robot_id": ROBOT_ID,
                "token": "rft_9N2dKmX4pQvR7sT1wYzA3bC6dE8fG0hI1jK4lM5nO6p",
                "created_at": "2026-11-09T08:21:30Z",
                "expires_at": "2026-11-09T08:36:30Z",
            },
        )

    monkeypatch.setattr(httpx, "Client", _client_factory(handler))
    result = runner.invoke(app, ["robot", "enrollment-token", ROBOT_ID])
    assert result.exit_code == 0
    assert "rft_9N2dKmX4pQvR7sT1wYzA3bC6dE8fG0hI1jK4lM5nO6p" in result.output
    assert "shown exactly once" in result.output


def test_rotate_prints_new_secret(monkeypatch, keyring):
    def handler(request: httpx.Request) -> httpx.Response:
        assert request.url.path == f"/v1/robots/{ROBOT_ID}/credentials"
        return httpx.Response(
            201,
            json={
                "credential": {
                    "id": CREDENTIAL_ID,
                    "robot_id": ROBOT_ID,
                    "created_at": "2026-11-09T09:05:12Z",
                    "expires_at": None,
                    "revoked_at": None,
                },
                "secret": "rfs_PqR7sT1wYzA3bC6dE8fG0hI1jK4lM5nO6p9N2dKmX4Q",
            },
        )

    monkeypatch.setattr(httpx, "Client", _client_factory(handler))
    result = runner.invoke(app, ["robot", "rotate", ROBOT_ID])
    assert result.exit_code == 0
    assert CREDENTIAL_ID in result.output
    assert "rfs_PqR7sT1wYzA3bC6dE8fG0hI1jK4lM5nO6p9N2dKmX4Q" in result.output


def test_credentials_and_revoke(monkeypatch, keyring):
    credential = {
        "id": CREDENTIAL_ID,
        "robot_id": ROBOT_ID,
        "created_at": "2026-11-09T08:22:40Z",
        "expires_at": None,
        "revoked_at": "2026-11-09T09:30:00Z",
    }

    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path.endswith("/revoke"):
            return httpx.Response(200, json=credential)
        return httpx.Response(200, json={"items": [credential]})

    monkeypatch.setattr(httpx, "Client", _client_factory(handler))
    listing = runner.invoke(app, ["robot", "credentials", ROBOT_ID])
    assert listing.exit_code == 0
    assert "3c4d5e6f" in listing.output

    revoked = runner.invoke(app, ["robot", "revoke", ROBOT_ID, CREDENTIAL_ID])
    assert revoked.exit_code == 0
    assert f"Revoked credential {CREDENTIAL_ID}" in revoked.output


def test_forbidden_error_is_reported(monkeypatch, keyring):
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            403,
            json={
                "error": {
                    "code": "forbidden",
                    "message": "This operation requires the Owner role.",
                    "request_id": "x",
                }
            },
        )

    monkeypatch.setattr(httpx, "Client", _client_factory(handler))
    result = runner.invoke(app, ["robot", "enrollment-token", ROBOT_ID])
    assert result.exit_code == 1
    assert "403 forbidden" in result.output


def test_robot_status_shows_observed_state(monkeypatch, keyring):
    def online(request: httpx.Request) -> httpx.Response:
        assert request.url.path == f"/v1/robots/{ROBOT_ID}/status"
        return httpx.Response(
            200,
            json={
                "robot_id": ROBOT_ID,
                "online": True,
                "last_seen_at": "2026-11-10T09:00:14Z",
                "daemon_version": "0.1.0",
                "protocol_version": 1,
            },
        )

    monkeypatch.setattr(httpx, "Client", _client_factory(online))
    result = runner.invoke(app, ["robot", "status", ROBOT_ID])
    assert result.exit_code == 0
    assert "online" in result.output
    assert "2026-11-10T09:00:14Z" in result.output
    assert "0.1.0" in result.output

    def offline(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            200,
            json={
                "robot_id": ROBOT_ID,
                "online": False,
                "last_seen_at": None,
                "daemon_version": None,
                "protocol_version": None,
            },
        )

    monkeypatch.setattr(httpx, "Client", _client_factory(offline))
    result = runner.invoke(app, ["robot", "status", ROBOT_ID])
    assert result.exit_code == 0
    assert "offline" in result.output
    assert "never" in result.output


def test_daemon_enroll_success(monkeypatch, env):
    seen = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["url"] = str(request.url)
        seen["body"] = json.loads(request.content)
        return httpx.Response(
            200,
            json={
                "robot_id": ROBOT_ID,
                "organization_id": ORG_ID,
                "credential_id": CREDENTIAL_ID,
            },
        )

    monkeypatch.setattr(httpx, "Client", _client_factory(handler))
    result = runner.invoke(app, ["daemon", "enroll", "rft_some-token"])
    assert result.exit_code == 0
    assert seen["url"] == "http://localhost:8081/v1/enroll"
    assert seen["body"] == {"token": "rft_some-token"}
    assert "Device enrolled" in result.output
    assert ROBOT_ID in result.output


def test_daemon_enroll_reports_rejection(monkeypatch, env):
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(401, json={"detail": "enrollment token rejected by the platform"})

    monkeypatch.setattr(httpx, "Client", _client_factory(handler))
    result = runner.invoke(app, ["daemon", "enroll", "rft_bad"])
    assert result.exit_code == 1
    assert "enrollment token rejected" in result.output


def test_daemon_identity_states(monkeypatch, env):
    def enrolled(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            200,
            json={
                "enrolled": True,
                "robot_id": ROBOT_ID,
                "organization_id": ORG_ID,
                "platform_url": "http://api:8080",
                "credential_id": CREDENTIAL_ID,
                "error": None,
            },
        )

    monkeypatch.setattr(httpx, "Client", _client_factory(enrolled))
    result = runner.invoke(app, ["daemon", "identity"])
    assert result.exit_code == 0
    assert ROBOT_ID in result.output

    def not_enrolled(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"enrolled": False, "error": None})

    monkeypatch.setattr(httpx, "Client", _client_factory(not_enrolled))
    result = runner.invoke(app, ["daemon", "identity"])
    assert result.exit_code == 0
    assert "not enrolled" in result.output


def test_daemon_unreachable(monkeypatch, env):
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("connection refused", request=request)

    monkeypatch.setattr(httpx, "Client", _client_factory(handler))
    result = runner.invoke(app, ["daemon", "identity"])
    assert result.exit_code == 1
    assert "Daemon unreachable" in result.output
