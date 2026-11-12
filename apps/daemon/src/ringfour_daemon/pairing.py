"""Device-initiated pairing. The pending secret never leaves protected storage except as proof over TLS."""
import asyncio
import base64
import hashlib
import json
import logging
import secrets
import uuid
from pathlib import Path

import httpx

from .enrollment import _write_credential, load_identity, save_identity

logger = logging.getLogger(__name__)


def pending_path(settings):
    return Path(settings.credential_path).with_name("pairing-pending.json")


def hash_value(value):
    return hashlib.sha256(value.encode()).hexdigest()


def create_pending(settings):
    code = base64.b32encode(secrets.token_bytes(16)).decode().rstrip("=")
    secret = "rfs_" + base64.urlsafe_b64encode(secrets.token_bytes(32)).decode().rstrip("=")
    pending = {"request_id": str(uuid.uuid4()), "code": code, "secret": secret,
               "platform_url": settings.platform_url}
    _write_credential(str(pending_path(settings)), json.dumps(pending))
    return pending


def format_code(code):
    return "-".join(code[index:index + 4] for index in range(0, len(code), 4))


async def run_pairing(settings, *, sleep=asyncio.sleep, client_factory=httpx.AsyncClient):
    path = pending_path(settings)
    announced = None
    while True:
        try:
            if load_identity(settings.db_path) is not None:
                if path.exists():
                    path.unlink()
                await sleep(5)
                continue
            if path.exists():
                pending = json.loads(path.read_text())
                if pending["platform_url"] != settings.platform_url:
                    if "expires_at" not in pending:
                        # No request was confirmed and no code was displayed.
                        # Discard the unregistered request when fixing the URL.
                        path.unlink(missing_ok=True)
                        announced = None
                        continue
                    logger.error("Pending pairing belongs to a different platform; restore the original platform URL")
                    await sleep(30)
                    continue
            else:
                pending = create_pending(settings)
            async with client_factory(base_url=settings.platform_url, timeout=5) as client:
                response = await client.post("/v1/device/pairing-requests", json={
                    "request_id": pending["request_id"], "code_hash": hash_value(pending["code"]),
                    "credential_hash": hash_value(pending["secret"]),
                })
                response.raise_for_status()
                expires_at = response.json()["expires_at"]
                if pending.get("expires_at") != expires_at:
                    pending["expires_at"] = expires_at
                    _write_credential(str(path), json.dumps(pending))
                if announced != pending["request_id"]:
                    print(f"Ringfour pairing code: {format_code(pending['code'])} (expires {expires_at})", flush=True)
                    announced = pending["request_id"]
                result = await client.post(f"/v1/device/pairing-requests/{pending['request_id']}/poll", json={"credential_secret": pending["secret"]})
                if result.status_code == 410:
                    path.unlink(missing_ok=True)
                    announced = None
                    continue
                result.raise_for_status()
                body = result.json()
                if body["state"] == "claimed":
                    if save_identity(settings, {
                            "robot_id": body["robot_id"], "organization_id": body["organization_id"],
                            "platform_url": settings.platform_url, "credential_id": body["credential_id"],
                        }, pending["secret"]):
                        logger.info("Pairing completed for robot %s", body["robot_id"])
                    path.unlink(missing_ok=True)
        except httpx.ConnectError:
            logger.warning(
                "Cannot connect to the Ringfour platform; check PLATFORM_API_URL "
                "and network connectivity. Retrying in 5 seconds."
            )
        except httpx.HTTPStatusError as error:
            logger.warning(
                "Platform rejected the pairing request (HTTP %s); retrying in 5 seconds.",
                error.response.status_code,
            )
        except (httpx.HTTPError, OSError, ValueError, KeyError) as error:
            logger.warning("Pairing is temporarily unavailable (%s); retrying in 5 seconds.",
                           type(error).__name__)
        await sleep(5)
