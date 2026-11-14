"""Mission-authored Logbook entries through the local Ringfour runtime.

The runtime supplies the execution-scoped endpoint and credential. These are
separate from the Agent connection and the SDK's ``robot.audit`` API.
"""
from __future__ import annotations

import http.client
import json
import math
import os
import re
import urllib.error
import urllib.parse
import urllib.request
import uuid


class LogbookError(RuntimeError):
    """An entry was invalid or could not be durably acknowledged."""


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def _request(message: str, fields: dict) -> tuple[str, bytes]:
    if not isinstance(message, str) or not message.strip() or "\x00" in message or len(message) > 500:
        raise LogbookError("Logbook message must contain 1–500 characters.")
    if len(fields) > 16:
        raise LogbookError("Logbook entries allow at most 16 fields.")
    for key, value in fields.items():
        if not re.fullmatch(r"[a-zA-Z][a-zA-Z0-9_]{0,63}", key):
            raise LogbookError("Invalid Logbook field name.")
        if value is None or type(value) is bool:
            continue
        if type(value) is int and -(2 ** 53 - 1) <= value <= 2 ** 53 - 1:
            continue
        if isinstance(value, str) and len(value) <= 512 and "\x00" not in value:
            continue
        if type(value) is float and math.isfinite(value) and abs(value) <= 2 ** 53 - 1:
            continue
        raise LogbookError("Logbook fields must be bounded strings or JSON scalars.")
    entry_id = str(uuid.uuid4())
    try:
        body = json.dumps({"entry_id": entry_id, "level": "info", "message": message, "fields": fields},
                          ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8")
    except (TypeError, ValueError, UnicodeError):
        raise LogbookError("Logbook entry cannot be encoded as JSON.") from None
    if len(body) > 8192:
        raise LogbookError("Logbook entry exceeds 8192 bytes.")
    return entry_id, body


def info(message: str, **fields: object) -> str:
    """Record one meaningful entry and return its ID after local durable acknowledgement.

    Raises LogbookError when running outside a configured Ringfour execution or
    if the runtime rejects the entry. A transport retry uses the same entry ID.
    """
    entry_id, body = _request(message, fields)
    base = os.environ.get("RINGFOUR_LOGBOOK_URL", "")
    token = os.environ.get("RINGFOUR_LOGBOOK_TOKEN", "")
    try:
        parts = urllib.parse.urlsplit(base)
        if (parts.scheme not in ("http", "https") or not parts.hostname or parts.username is not None
                or parts.password is not None or parts.path not in ("", "/") or parts.query or parts.fragment
                or not token or "\n" in token or "\r" in token):
            raise ValueError
        _ = parts.port
    except ValueError:
        raise LogbookError("A configured Ringfour Logbook runtime is required.") from None
    request = urllib.request.Request(base.rstrip("/") + "/v1/runtime/logbook/entries", data=body,
        headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"}, method="POST")
    opener = urllib.request.build_opener(_NoRedirect(), urllib.request.ProxyHandler({}))
    for attempt in range(2):
        try:
            with opener.open(request, timeout=5) as response:
                if response.status != 200:
                    raise LogbookError("The Logbook runtime did not accept the entry.")
                raw = response.read(4097)
                if len(raw) > 4096:
                    raise LogbookError("The Logbook runtime returned an invalid acknowledgement.")
                payload = json.loads(raw)
            if not isinstance(payload, dict) or payload != {"entry_id": entry_id}:
                raise LogbookError("The Logbook runtime returned an invalid acknowledgement.")
            return entry_id
        except urllib.error.HTTPError as exc:
            raise LogbookError(f"The Logbook runtime rejected the entry (HTTP {exc.code}).") from None
        except (OSError, ValueError, UnicodeError, http.client.HTTPException):
            if attempt:
                raise LogbookError("The Logbook runtime could not acknowledge the entry.") from None
    raise LogbookError("The Logbook runtime could not acknowledge the entry.")
