import logging
import os
import sqlite3
import stat
import tempfile
import threading
from pathlib import Path

import httpx
from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel

logger = logging.getLogger(__name__)

router = APIRouter()

CREDENTIAL_MISSING = "credential_missing"
_identity_lock = threading.Lock()


class EnrollRequest(BaseModel):
    token: str


def load_identity(db_path: str) -> dict | None:
    with sqlite3.connect(db_path) as conn:
        row = conn.execute(
            "SELECT robot_id, organization_id, platform_url, credential_id"
            " FROM device_identity WHERE singleton_id = 1"
        ).fetchone()
    if row is None:
        return None
    return {
        "robot_id": row[0],
        "organization_id": row[1],
        "platform_url": row[2],
        "credential_id": row[3],
    }


def credential_error(credential_path: str) -> str | None:
    try:
        content = Path(credential_path).read_text().strip()
    except OSError:
        return CREDENTIAL_MISSING
    return None if content else CREDENTIAL_MISSING


def check_device_state(db_path: str, credential_path: str) -> None:
    """Boot-time verification: a committed identity without its secret is a
    broken device and must be reported loudly."""
    identity = load_identity(db_path)
    if identity is not None and credential_error(credential_path) is not None:
        logger.error(
            "device identity for robot %s exists but the credential at %s is missing or empty",
            identity["robot_id"],
            credential_path,
        )


def _write_credential(credential_path: str, secret: str) -> None:
    """Store the usable secret in protected storage (owner-only permissions),
    atomically. The identity row is committed only after this succeeds."""
    path = Path(credential_path)
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temp_name = tempfile.mkstemp(dir=path.parent, prefix=".credential-")
    try:
        os.fchmod(fd, stat.S_IRUSR | stat.S_IWUSR)
        with os.fdopen(fd, "w") as handle:
            handle.write(secret)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temp_name, path)
    except BaseException:
        try:
            os.unlink(temp_name)
        except OSError:
            pass
        raise


def _commit_identity(db_path: str, identity: dict) -> None:
    with sqlite3.connect(db_path) as conn:
        conn.execute(
            "INSERT INTO device_identity"
            " (singleton_id, robot_id, organization_id, platform_url, credential_id)"
            " VALUES (1, ?, ?, ?, ?)",
            (
                identity["robot_id"],
                identity["organization_id"],
                identity["platform_url"],
                identity["credential_id"],
            ),
        )


def save_identity(settings, identity: dict, secret: str) -> bool:
    """Serialize both enrollment paths so one cannot overwrite the other's secret."""
    with _identity_lock:
        if load_identity(settings.db_path) is not None:
            return False
        _write_credential(settings.credential_path, secret)
        _commit_identity(settings.db_path, identity)
        return True


@router.post("/v1/enroll")
def enroll(request: Request, body: EnrollRequest) -> JSONResponse:
    settings = request.app.state.settings
    if load_identity(settings.db_path) is not None:
        return JSONResponse(
            {"detail": "device is already enrolled"}, status_code=409
        )

    try:
        with request.app.state.http_client_factory() as client:
            response = client.post(
                f"{settings.platform_url}/v1/device/enroll", json={"token": body.token}
            )
    except httpx.HTTPError:
        return JSONResponse({"detail": "platform unreachable"}, status_code=502)

    if response.status_code == 401:
        return JSONResponse(
            {"detail": "enrollment token rejected by the platform"}, status_code=401
        )
    if response.status_code != 200:
        return JSONResponse(
            {"detail": f"unexpected platform response: {response.status_code}"},
            status_code=502,
        )

    payload = response.json()
    identity = {
        "robot_id": payload["robot_id"],
        "organization_id": payload["organization_id"],
        "platform_url": settings.platform_url,
        "credential_id": payload["credential_id"],
    }
    # Secret first, binding second: never commit an identity whose credential
    # is not safely stored.
    if not save_identity(settings, identity, payload["credential_secret"]):
        return JSONResponse({"detail": "device is already enrolled"}, status_code=409)
    return JSONResponse(
        {
            "robot_id": identity["robot_id"],
            "organization_id": identity["organization_id"],
            "credential_id": identity["credential_id"],
        }
    )


@router.get("/v1/identity")
def identity(request: Request) -> dict:
    settings = request.app.state.settings
    stored = load_identity(settings.db_path)
    if stored is None:
        return {"enrolled": False, "error": None}
    return {
        "enrolled": True,
        "robot_id": stored["robot_id"],
        "organization_id": stored["organization_id"],
        "platform_url": stored["platform_url"],
        "credential_id": stored["credential_id"],
        "error": credential_error(settings.credential_path),
    }
