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
def settings(env):
    return load_settings()


@pytest.fixture()
def keyring(monkeypatch):
    fake = FakeKeyring()
    monkeypatch.setattr(auth, "TokenStore", lambda: REAL_TOKEN_STORE(fake))
    return fake


def _client_factory(handler):
    def factory(*args, **kwargs):
        kwargs["transport"] = httpx.MockTransport(handler)
        return REAL_CLIENT(*args, **kwargs)

    return factory


def test_token_store_roundtrip_and_clear(settings):
    fake = FakeKeyring()
    store = auth.TokenStore(fake)
    tokens = {"access_token": "at", "refresh_token": "rt"}

    store.save(settings, tokens)
    assert store.load(settings) == tokens
    assert store.clear(settings) is True
    assert store.load(settings) is None
    assert store.clear(settings) is False


def test_request_device_code_posts_client_scope_and_audience(settings):
    seen = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["url"] = str(request.url)
        seen["body"] = dict(httpx.QueryParams(request.content.decode()))
        return httpx.Response(
            200,
            json={
                "device_code": "dc",
                "user_code": "ABCD-EFGH",
                "verification_uri": "https://tenant.eu.auth0.com/activate",
                "interval": 1,
                "expires_in": 60,
            },
        )

    with httpx.Client(transport=httpx.MockTransport(handler)) as client:
        device = auth.request_device_code(client, settings)

    assert seen["url"] == "https://tenant.eu.auth0.com/oauth/device/code"
    assert seen["body"]["client_id"] == "cli-client-id"
    assert seen["body"]["audience"] == "https://api.ringfour.eu"
    assert "offline_access" in seen["body"]["scope"]
    assert device["device_code"] == "dc"


def test_poll_waits_through_pending_then_succeeds(monkeypatch, settings):
    monkeypatch.setattr("time.sleep", lambda _: None)
    calls = []

    def handler(request: httpx.Request) -> httpx.Response:
        calls.append(request)
        if len(calls) < 3:
            return httpx.Response(403, json={"error": "authorization_pending"})
        return httpx.Response(200, json={"access_token": "at", "refresh_token": "rt"})

    device = {"device_code": "dc", "interval": 1, "expires_in": 60}
    with httpx.Client(transport=httpx.MockTransport(handler)) as client:
        tokens = auth.poll_for_tokens(client, settings, device)

    assert tokens["access_token"] == "at"
    assert len(calls) == 3


def test_poll_access_denied_raises(monkeypatch, settings):
    monkeypatch.setattr("time.sleep", lambda _: None)

    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(403, json={"error": "access_denied"})

    device = {"device_code": "dc", "interval": 1, "expires_in": 60}
    with (
        httpx.Client(transport=httpx.MockTransport(handler)) as client,
        pytest.raises(auth.DeviceAuthError, match="denied"),
    ):
        auth.poll_for_tokens(client, settings, device)


def test_refresh_preserves_refresh_token_when_not_rotated(settings):
    def handler(request: httpx.Request) -> httpx.Response:
        body = dict(httpx.QueryParams(request.content.decode()))
        assert body["grant_type"] == "refresh_token"
        assert body["refresh_token"] == "rt-old"
        return httpx.Response(200, json={"access_token": "at-new"})

    with httpx.Client(transport=httpx.MockTransport(handler)) as client:
        refreshed = auth.refresh_tokens(
            client, settings, {"access_token": "at-old", "refresh_token": "rt-old"}
        )

    assert refreshed == {"access_token": "at-new", "refresh_token": "rt-old"}


def test_login_command_stores_tokens_in_credential_store(monkeypatch, settings, keyring, env):
    monkeypatch.setattr(
        auth,
        "request_device_code",
        lambda client, config: {
            "device_code": "dc",
            "user_code": "ABCD-EFGH",
            "verification_uri": "https://tenant.eu.auth0.com/activate",
        },
    )
    monkeypatch.setattr(
        auth,
        "poll_for_tokens",
        lambda client, config, device: {"access_token": "at", "refresh_token": "rt"},
    )

    result = runner.invoke(app, ["login"])

    assert result.exit_code == 0
    assert "ABCD-EFGH" in result.output
    stored = list(keyring.store.values())
    assert stored == [json.dumps({"access_token": "at", "refresh_token": "rt"})]
    config_path = env / "config.json"
    if config_path.exists():
        assert "access_token" not in config_path.read_text()


def test_logout_command_clears_credentials(settings, keyring):
    REAL_TOKEN_STORE(keyring).save(settings, {"access_token": "at"})

    result = runner.invoke(app, ["logout"])

    assert result.exit_code == 0
    assert keyring.store == {}

    result = runner.invoke(app, ["logout"])
    assert "No stored credentials" in result.output


def test_whoami_refreshes_expired_token_once(monkeypatch, settings, keyring):
    REAL_TOKEN_STORE(keyring).save(settings, {"access_token": "at-old", "refresh_token": "rt"})

    me_calls = []

    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path == "/v1/me":
            me_calls.append(request.headers.get("authorization"))
            if len(me_calls) == 1:
                return httpx.Response(
                    401,
                    json={"error": {"code": "unauthenticated", "message": "x", "request_id": "r"}},
                )
            return httpx.Response(
                200,
                json={
                    "user": {"id": "u-1", "created_at": "2026-11-09T08:15:30Z"},
                    "memberships": [],
                },
            )
        return httpx.Response(200, json={"access_token": "at-new"})

    monkeypatch.setattr(httpx, "Client", _client_factory(handler))

    result = runner.invoke(app, ["whoami"])

    assert result.exit_code == 0
    assert "u-1" in result.output
    assert me_calls == ["Bearer at-old", "Bearer at-new"]
    assert REAL_TOKEN_STORE(keyring).load(settings)["access_token"] == "at-new"


def test_org_select_persists_selection_not_secrets(monkeypatch, settings, keyring, env):
    REAL_TOKEN_STORE(keyring).save(settings, {"access_token": "at-secret", "refresh_token": "rt-secret"})

    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            200,
            json={
                "items": [
                    {
                        "id": "9b8a7c6d-5e4f-4a3b-8c7d-6e5f4a3b2c1d",
                        "name": "Acme Robotics",
                        "role": "owner",
                        "created_at": "2026-11-09T08:16:02Z",
                    }
                ],
                "next_cursor": None,
            },
        )

    monkeypatch.setattr(httpx, "Client", _client_factory(handler))

    result = runner.invoke(app, ["org", "select", "9b8a7c6d-5e4f-4a3b-8c7d-6e5f4a3b2c1d"])

    assert result.exit_code == 0
    config_text = (env / "config.json").read_text()
    assert "9b8a7c6d-5e4f-4a3b-8c7d-6e5f4a3b2c1d" in config_text
    assert "at-secret" not in config_text
    assert "rt-secret" not in config_text


def test_org_select_rejects_non_membership(monkeypatch, settings, keyring, env):
    REAL_TOKEN_STORE(keyring).save(settings, {"access_token": "at"})

    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"items": [], "next_cursor": None})

    monkeypatch.setattr(httpx, "Client", _client_factory(handler))

    result = runner.invoke(app, ["org", "select", "9b8a7c6d-5e4f-4a3b-8c7d-6e5f4a3b2c1d"])

    assert result.exit_code == 1
    assert not (env / "config.json").exists()
