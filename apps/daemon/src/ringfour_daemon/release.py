"""Recover a server-released enrollment at startup, before runtime tasks begin.

Ordinary credential expiry/revocation and connectivity failures are not releases.
Old enrollment data remains in a protected SQLite archive, never in a new binding.
"""

import fcntl
import logging
import os
import sqlite3
import tempfile
import time
from contextlib import closing
from pathlib import Path

import httpx

from .db import migrate
from .enrollment import _identity_lock, load_identity

logger = logging.getLogger(__name__)
REASONS = {"robot_unenrolled", "robot_deleted", "organization_deleted"}


def recover_released_enrollment(settings, client_factory=None):
    identity = load_identity(settings.db_path)
    if identity is None:
        return None
    try:
        secret = Path(settings.credential_path).read_text().strip()
        if not secret:
            return None
        with (
            client_factory()
            if client_factory
            else httpx.Client(timeout=3, trust_env=False)
        ) as client:
            response = client.post(
                identity["platform_url"].rstrip("/") + "/v1/device/enrollment-state",
                json={"credential_id": identity["credential_id"], "secret": secret},
            )
        if response.status_code != 200:
            return None
        receipt = response.json()
        if not isinstance(receipt, dict):
            return None
        if (
            receipt.get("state") != "released"
            or receipt.get("reason") not in REASONS
            or receipt.get("robot_id") != identity["robot_id"]
            or receipt.get("credential_id") != identity["credential_id"]
        ):
            return None
        return archive_released_enrollment(settings, identity)
    except (httpx.HTTPError, OSError, ValueError, sqlite3.Error) as error:
        logger.warning(
            "Enrollment release recovery unavailable (%s); retaining the current binding",
            type(error).__name__,
        )
        return None


def archive_released_enrollment(settings, expected):
    """Startup-only operation: excludes another App writer and preserves old state."""
    from .app_engine import Engine, EngineError

    with _identity_lock:
        if load_identity(settings.db_path) != expected:
            return None
        lock = os.open(
            settings.db_path + ".apps.lock",
            os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW,
            0o600,
        )
        fresh = None
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            if settings.apps_enabled:
                engine = Engine(settings)
                try:
                    if engine.owned(
                        expected["robot_id"], expected["deployment_namespace"]
                    ):
                        logger.error(
                            "Released enrollment still owns App containers; retaining state for operator recovery"
                        )
                        return None
                except EngineError:
                    logger.warning(
                        "Cannot inspect released App containers; retaining state for operator recovery"
                    )
                    return None
                finally:
                    engine.close()
            database = Path(settings.db_path)
            history = database.parent / "enrollment-history"
            history.mkdir(mode=0o700, exist_ok=True)
            os.chmod(history, 0o700)
            archive = history / f"enrollment-{time.time_ns()}.sqlite"
            descriptor = os.open(archive, os.O_CREAT | os.O_EXCL | os.O_RDWR, 0o600)
            os.close(descriptor)
            with (
                closing(sqlite3.connect(database)) as source,
                closing(sqlite3.connect(archive)) as target,
            ):
                source.backup(target)
                source.execute("PRAGMA wal_checkpoint(TRUNCATE)")
            descriptor, name = tempfile.mkstemp(
                prefix=".new-enrollment-", dir=database.parent
            )
            os.close(descriptor)
            fresh = Path(name)
            migrate(str(fresh))
            for suffix in ("-wal", "-shm"):
                Path(str(database) + suffix).unlink(missing_ok=True)
            os.replace(fresh, database)
            fresh = None
            Path(settings.credential_path).unlink(missing_ok=True)
            Path(settings.credential_path).with_name("pairing-pending.json").unlink(
                missing_ok=True
            )
            logger.info(
                "Released enrollment archived at %s; ready for new pairing", archive
            )
            return archive
        finally:
            if fresh is not None:
                fresh.unlink(missing_ok=True)
            os.close(lock)
