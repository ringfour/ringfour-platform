"""Publish immutable execution artifacts through the local Ringfour runtime."""
from __future__ import annotations

import http.client
import json
import mimetypes
import os
from pathlib import Path
import urllib.error
import urllib.parse
import urllib.request
import uuid


class ArtifactError(RuntimeError):
    """A file was invalid or could not be durably queued locally."""


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def publish_file(path: str | os.PathLike[str], *, content_type: str | None = None,
                 artifact_id: str | None = None) -> str:
    """Snapshot and queue a file, returning its stable artifact ID.

    Success is a local durability acknowledgement, not proof that the Platform
    has received the file. Retrying with ``artifact_id`` is idempotent.
    """
    source = Path(path)
    root_text = os.environ.get("RINGFOUR_ARTIFACT_DIR", "")
    try:
        root = Path(root_text).resolve(strict=True)
        relative = source.resolve(strict=True).relative_to(root).as_posix()
    except (OSError, ValueError):
        raise ArtifactError("Artifact path must name a file under RINGFOUR_ARTIFACT_DIR.") from None
    if not source.is_file() or source.is_symlink():
        raise ArtifactError("Artifact path must name a regular file, not a symlink.")
    candidate = content_type or mimetypes.guess_type(source.name)[0] or "application/octet-stream"
    if candidate not in {"image/jpeg", "image/png", "application/octet-stream"}:
        raise ArtifactError("Unsupported artifact content type.")
    identifier = artifact_id or str(uuid.uuid4())
    try:
        if str(uuid.UUID(identifier)) != identifier:
            raise ValueError
    except (ValueError, AttributeError):
        raise ArtifactError("Artifact ID must be a lowercase UUID.") from None
    base = os.environ.get("RINGFOUR_ARTIFACT_URL") or os.environ.get("RINGFOUR_LOGBOOK_URL", "")
    token = os.environ.get("RINGFOUR_ARTIFACT_TOKEN") or os.environ.get("RINGFOUR_LOGBOOK_TOKEN", "")
    try:
        parts = urllib.parse.urlsplit(base)
        if (parts.scheme not in ("http", "https") or not parts.hostname or parts.username is not None
                or parts.password is not None or parts.path not in ("", "/") or parts.query or parts.fragment
                or not token or "\n" in token or "\r" in token):
            raise ValueError
        _ = parts.port
    except ValueError:
        raise ArtifactError("A configured Ringfour artifact runtime is required.") from None
    body = json.dumps({"artifact_id": identifier, "path": relative, "content_type": candidate},
                     separators=(",", ":")).encode()
    request = urllib.request.Request(base.rstrip("/") + "/v1/runtime/artifacts", data=body,
        headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"}, method="POST")
    opener = urllib.request.build_opener(_NoRedirect(), urllib.request.ProxyHandler({}))
    for attempt in range(2):
        try:
            with opener.open(request, timeout=30) as response:
                payload = json.loads(response.read(8193))
            if not isinstance(payload, dict) or payload.get("artifact_id") != identifier:
                raise ArtifactError("The artifact runtime returned an invalid acknowledgement.")
            return identifier
        except urllib.error.HTTPError as exc:
            raise ArtifactError(f"The artifact runtime rejected the file (HTTP {exc.code}).") from None
        except (OSError, ValueError, UnicodeError, http.client.HTTPException):
            if attempt:
                raise ArtifactError("The artifact runtime could not acknowledge the file.") from None
    raise ArtifactError("The artifact runtime could not acknowledge the file.")
