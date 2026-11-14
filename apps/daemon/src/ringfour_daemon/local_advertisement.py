"""Publish a local TLS pin over the robot-authenticated Platform channel."""
import asyncio
import logging
from pathlib import Path

import httpx

from .enrollment import credential_error, load_identity
from .local_identity import local_identity

logger = logging.getLogger(__name__)


async def publish_local_endpoint(settings, *, sleep=asyncio.sleep):
    if not settings.local_dev_enabled:
        return
    fingerprint = local_identity(settings.local_tls_path)
    while True:
        try:
            identity = load_identity(settings.db_path)
            if identity is not None and not credential_error(settings.credential_path):
                headers = {"x-ringfour-credential-id": identity["credential_id"],
                           "x-ringfour-credential-secret": Path(settings.credential_path).read_text().strip()}
                async with httpx.AsyncClient(base_url=identity["platform_url"], headers=headers, timeout=5) as client:
                    response = await client.put("/v1/device/local-dev-endpoint", json={
                        "certificate_sha256": fingerprint, "daemon_url": settings.local_advertised_url,
                    })
                    response.raise_for_status()
        except (httpx.HTTPError, OSError):
            logger.warning("Could not advertise the local development endpoint; retrying.")
        await sleep(30)
