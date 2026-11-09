"""Auth0 device authorization and credential storage.

Tokens live in the OS credential store via keyring; the config file only ever
holds non-secret settings. Raw tokens are never printed.
"""

import json
import time

import httpx
import keyring

from .config import Settings

SERVICE_NAME = "ringfour-cli"
DEVICE_GRANT = "urn:ietf:params:oauth:grant-type:device_code"
SCOPE = "openid profile email offline_access"


class DeviceAuthError(Exception):
    pass


class TokenStore:
    def __init__(self, keyring_module=None):
        self._keyring = keyring_module or keyring

    def _account(self, settings: Settings) -> str:
        return f"{settings.auth0_domain}|{settings.auth0_audience}"

    def save(self, settings: Settings, tokens: dict) -> None:
        self._keyring.set_password(SERVICE_NAME, self._account(settings), json.dumps(tokens))

    def load(self, settings: Settings) -> dict | None:
        raw = self._keyring.get_password(SERVICE_NAME, self._account(settings))
        return json.loads(raw) if raw else None

    def clear(self, settings: Settings) -> bool:
        if self.load(settings) is None:
            return False
        self._keyring.delete_password(SERVICE_NAME, self._account(settings))
        return True


def request_device_code(client: httpx.Client, settings: Settings) -> dict:
    response = client.post(
        f"https://{settings.auth0_domain}/oauth/device/code",
        data={
            "client_id": settings.auth0_client_id,
            "scope": SCOPE,
            "audience": settings.auth0_audience,
        },
    )
    if response.status_code != 200:
        raise DeviceAuthError(
            f"Device authorization request failed ({response.status_code}): {response.text}"
        )
    return response.json()


def poll_for_tokens(client: httpx.Client, settings: Settings, device: dict) -> dict:
    interval = device.get("interval", 5)
    deadline = time.monotonic() + device.get("expires_in", 900)
    while time.monotonic() < deadline:
        response = client.post(
            f"https://{settings.auth0_domain}/oauth/token",
            data={
                "grant_type": DEVICE_GRANT,
                "device_code": device["device_code"],
                "client_id": settings.auth0_client_id,
            },
        )
        if response.status_code == 200:
            return response.json()
        error = response.json().get("error", "")
        if error == "authorization_pending":
            time.sleep(interval)
        elif error == "slow_down":
            interval += 5
            time.sleep(interval)
        elif error == "access_denied":
            raise DeviceAuthError("Authorization was denied.")
        elif error == "expired_token":
            raise DeviceAuthError("The device code expired before authorization completed.")
        else:
            raise DeviceAuthError(f"Token polling failed: {response.text}")
    raise DeviceAuthError("Timed out waiting for authorization.")


def refresh_tokens(client: httpx.Client, settings: Settings, tokens: dict) -> dict:
    refresh_token = tokens.get("refresh_token")
    if not refresh_token:
        raise DeviceAuthError("No refresh token stored; please log in again.")
    response = client.post(
        f"https://{settings.auth0_domain}/oauth/token",
        data={
            "grant_type": "refresh_token",
            "client_id": settings.auth0_client_id,
            "refresh_token": refresh_token,
        },
    )
    if response.status_code != 200:
        raise DeviceAuthError("Stored credentials are no longer valid; please log in again.")
    refreshed = response.json()
    if "refresh_token" not in refreshed:
        refreshed["refresh_token"] = refresh_token
    return refreshed
