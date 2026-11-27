"""Run-scoped live settings delivery for Ringfour Apps."""
from __future__ import annotations

import http.client
import json
import math
import os
import threading
import urllib.error
import urllib.parse
import urllib.request
import uuid
from decimal import Decimal, DecimalException
from collections.abc import Callable, Mapping
from dataclasses import dataclass


class SettingsError(RuntimeError):
    """The runtime rejected a settings request or returned an invalid value."""


class SettingsUnavailable(SettingsError):
    """The local runtime is temporarily unavailable; retry with the same ID."""


class SettingsRejected(SettingsError):
    """Raise from validation or the handler to reject an application safely."""

    def __init__(self, reason: str):
        self.reason = _safe_reason(reason)
        super().__init__(self.reason)


@dataclass(frozen=True)
class SettingsApplication:
    application_id: str
    settings_revision_id: str
    values: dict
    initial: bool
    outcome: str
    reason: str | None


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


_MAX_SAFE_NUMBER = 9007199254740991


def _json_number(token: str):
    try:
        exact = Decimal(token)
        value = float(exact)
        if not exact.is_finite() or abs(exact) > _MAX_SAFE_NUMBER or not math.isfinite(value):
            raise ValueError("unsafe settings number")
        if exact != 0 and value == 0:
            raise ValueError("settings number underflow")
        return int(value) if value.is_integer() and abs(value) <= _MAX_SAFE_NUMBER else value
    except (DecimalException, OverflowError) as error:
        raise ValueError("unsafe settings number") from error


def _validate_numbers(value):
    if type(value) is int:
        if abs(value) > _MAX_SAFE_NUMBER:
            raise ValueError("unsafe settings integer")
    elif type(value) is float:
        if not math.isfinite(value) or abs(value) > _MAX_SAFE_NUMBER:
            raise ValueError("unsafe settings number")
    elif isinstance(value, dict):
        for key, item in value.items():
            _validate_numbers(key)
            _validate_numbers(item)
    elif isinstance(value, list):
        for item in value:
            _validate_numbers(item)


def _runtime() -> tuple[str, str]:
    base = os.environ.get("RINGFOUR_SETTINGS_URL", "")
    token = os.environ.get("RINGFOUR_SETTINGS_TOKEN", "")
    try:
        parts = urllib.parse.urlsplit(base)
        if (parts.scheme not in ("http", "https") or not parts.hostname
                or parts.username is not None or parts.password is not None
                or parts.path not in ("", "/") or parts.query or parts.fragment
                or not token or "\n" in token or "\r" in token):
            raise ValueError
        _ = parts.port
    except ValueError:
        raise SettingsError("A configured Ringfour settings runtime is required.") from None
    return base.rstrip("/"), token


def _unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON key")
        result[key] = value
    return result


def _call(method: str, path: str, body: dict | None = None) -> dict:
    base, token = _runtime()
    encoded = None if body is None else json.dumps(
        body, ensure_ascii=False, allow_nan=False, separators=(",", ":")
    ).encode("utf-8")
    if body is not None:
        _validate_numbers(body)
    if encoded is not None and len(encoded) > 16_384:
        raise SettingsError("The settings request exceeds the runtime size limit.")
    request = urllib.request.Request(
        base + path,
        data=encoded,
        headers={
            "Authorization": f"Bearer {token}",
            **({"Content-Type": "application/json"} if encoded is not None else {}),
        },
        method=method,
    )
    try:
        opener = urllib.request.build_opener(_NoRedirect(), urllib.request.ProxyHandler({}))
        with opener.open(request, timeout=5) as response:
            raw = response.read(16_385)
        if len(raw) > 16_384:
            raise ValueError
        value = json.loads(
            raw,
            object_pairs_hook=_unique,
            parse_int=_json_number,
            parse_float=_json_number,
            parse_constant=lambda _: (_ for _ in ()).throw(ValueError("non-JSON number")),
        )
        if not isinstance(value, dict):
            raise TypeError
        _validate_numbers(value)
        return value
    except urllib.error.HTTPError as exc:
        if exc.code in (502, 503, 504):
            raise SettingsUnavailable(
                f"The settings runtime is temporarily unavailable (HTTP {exc.code})."
            ) from None
        raise SettingsError(f"The settings runtime rejected the request (HTTP {exc.code}).") from None
    except (OSError, http.client.HTTPException):
        raise SettingsUnavailable("The settings runtime could not complete the request.") from None
    except (ValueError, TypeError, UnicodeError):
        raise SettingsError("The settings runtime returned an invalid response.") from None


def _uuid(value: str, field: str) -> str:
    try:
        if not isinstance(value, str) or str(uuid.UUID(value)) != value:
            raise ValueError
    except (AttributeError, TypeError, ValueError):
        raise SettingsError(f"The runtime returned an invalid {field}.") from None
    return value


def _safe_reason(value: str) -> str:
    if not isinstance(value, str) or not value or len(value) > 240 or any(
        ord(char) < 32 or ord(char) == 127 for char in value
    ):
        raise SettingsError("A rejection reason must be a safe single line of at most 240 characters.")
    return value


def _pending(value: dict) -> tuple[str, str, dict, bool] | None:
    if set(value) != {"pending"}:
        raise SettingsError("The settings runtime returned an invalid pending response.")
    item = value["pending"]
    if item is None:
        return None
    if not isinstance(item, dict) or set(item) != {
        "application_id", "settings_revision_id", "values", "initial"
    }:
        raise SettingsError("The settings runtime returned an invalid pending application.")
    application_id = _uuid(item["application_id"], "application ID")
    revision_id = _uuid(item["settings_revision_id"], "settings revision ID")
    values = item["values"]
    if not isinstance(values, dict) or not isinstance(item["initial"], bool):
        raise SettingsError("The settings runtime returned invalid settings values.")
    return application_id, revision_id, values, item["initial"]


class SettingsClient:
    """Poll and apply exact-run settings; cache results until the runtime acks them.

    The cache is deliberately in-process. Application handlers should still be
    idempotent for their own side effects because transport delivery is at least
    once and a process can stop after applying values but before reporting them.
    """

    def __init__(self):
        self._completed: dict[str, tuple[str, str | None]] = {}

    def poll_once(
        self,
        handler: Callable[[Mapping], None],
        *,
        validate: Callable[[Mapping], None] | None = None,
    ) -> SettingsApplication | None:
        """Apply one pending complete value set and report its result.

        ``validate`` runs before ``handler`` and can validate against the App's
        pinned schema. Raise :class:`SettingsRejected` for a bounded safe
        rejection reason. A missing pending item returns ``None``.
        """
        pending = _pending(_call("GET", "/v1/runtime/settings/pending"))
        if pending is None:
            return None
        application_id, revision_id, values, initial = pending
        if application_id not in self._completed:
            try:
                if validate is not None:
                    validate(values)
                handler(values)
                self._completed[application_id] = ("applied", None)
            except SettingsRejected as exc:
                self._completed[application_id] = ("rejected", exc.reason)
        outcome, reason = self._completed[application_id]
        _call(
            "POST",
            f"/v1/runtime/settings/{application_id}/result",
            {"outcome": outcome, **({"reason": reason} if reason is not None else {})},
        )
        self._completed.pop(application_id, None)
        return SettingsApplication(
            application_id, revision_id, values, initial, outcome, reason
        )

    def serve(
        self,
        handler: Callable[[Mapping], None],
        *,
        poll_seconds: float = 5,
        validate: Callable[[Mapping], None] | None = None,
        stop_event: threading.Event | None = None,
        on_application: Callable[[SettingsApplication], None] | None = None,
    ) -> None:
        """Poll until cancelled, retrying transient outages without reapplying.

        ``poll_seconds`` is clamped to the contract's 1–30 second interval.
        Set ``stop_event`` to stop cleanly between requests/retries.
        """
        delay = min(30.0, max(1.0, float(poll_seconds)))
        stop = stop_event or threading.Event()
        while not stop.is_set():
            try:
                result = self.poll_once(handler, validate=validate)
                if result is not None and on_application is not None:
                    on_application(result)
                wait = delay
            except SettingsUnavailable:
                wait = min(30.0, delay)
            if stop.wait(wait):
                return


def serve(handler: Callable[[Mapping], None], **kwargs) -> None:
    """Convenience wrapper using one :class:`SettingsClient` for this process."""
    SettingsClient().serve(handler, **kwargs)
