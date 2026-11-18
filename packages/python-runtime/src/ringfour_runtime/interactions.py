"""Durable human interactions through the local Ringfour runtime."""
from __future__ import annotations

import http.client
import json
import os
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from collections.abc import Callable, Mapping
from dataclasses import dataclass
from datetime import datetime, timezone


class InteractionError(RuntimeError):
    """An interaction was invalid or the local runtime could not serve it."""


class InteractionUnavailable(InteractionError):
    """A transient local runtime or transport outage that callers can retry."""


@dataclass(frozen=True)
class Interaction:
    request_id: str
    execution_id: str
    state: str
    outcome: str | None
    deadline: str | None
    timeout_outcome: str | None
    response: dict | None


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def _runtime() -> tuple[str, str]:
    base = os.environ.get("RINGFOUR_INTERACTION_URL") or os.environ.get("RINGFOUR_LOGBOOK_URL", "")
    token = os.environ.get("RINGFOUR_INTERACTION_TOKEN") or os.environ.get("RINGFOUR_LOGBOOK_TOKEN", "")
    try:
        parts = urllib.parse.urlsplit(base)
        if (parts.scheme not in ("http", "https") or not parts.hostname or parts.username is not None
                or parts.password is not None or parts.path not in ("", "/") or parts.query or parts.fragment
                or not token or "\n" in token or "\r" in token):
            raise ValueError
        _ = parts.port
    except ValueError:
        raise InteractionError("A configured Ringfour interaction runtime is required.") from None
    return base.rstrip("/"), token


def _call(method: str, path: str, body: dict | None = None) -> dict:
    base, token = _runtime()
    encoded = None if body is None else json.dumps(body, ensure_ascii=False, separators=(",", ":")).encode()
    request = urllib.request.Request(base + path, data=encoded,
        headers={"Authorization": f"Bearer {token}", **({"Content-Type": "application/json"} if encoded else {})},
        method=method)
    try:
        with urllib.request.build_opener(_NoRedirect(), urllib.request.ProxyHandler({})).open(request, timeout=35) as response:
            raw = response.read(32769)
        if len(raw) > 32768:
            raise ValueError
        value = json.loads(raw)
        if not isinstance(value, dict):
            raise TypeError
        return value
    except urllib.error.HTTPError as exc:
        if exc.code in (502, 503, 504):
            raise InteractionUnavailable(
                f"The interaction runtime is temporarily unavailable (HTTP {exc.code})."
            ) from None
        raise InteractionError(f"The interaction runtime rejected the request (HTTP {exc.code}).") from None
    except (OSError, http.client.HTTPException):
        raise InteractionUnavailable("The interaction runtime could not complete the request.") from None
    except (ValueError, UnicodeError):
        raise InteractionError("The interaction runtime returned an invalid response.") from None


def _result(value: dict) -> Interaction:
    try:
        return Interaction(request_id=value["request_id"], execution_id=value["execution_id"],
            state=value["state"], outcome=value["outcome"], deadline=value["deadline"],
            timeout_outcome=value["timeout_outcome"],
            response=value["response"])
    except (KeyError, TypeError):
        raise InteractionError("The interaction runtime returned an invalid response.") from None


def _request_id(value: str) -> str:
    try:
        if str(uuid.UUID(value)) != value:
            raise ValueError
    except (AttributeError, TypeError, ValueError):
        raise InteractionError("request_id must be a lowercase UUID.") from None
    return value


def create(prompt: str, choices: Mapping[str, str], *, deadline: datetime | None = None,
           timeout_outcome: str | None = None, request_id: str | None = None) -> Interaction:
    """Persist an interaction locally; use request_id to make caller retries stable.

    With no deadline, the interaction waits for a human indefinitely. When a
    deadline is provided, ``timeout_outcome`` must name a choice safe to apply
    without human approval. The daemon, not the hosted service, owns timeout.
    """
    if (deadline is None) != (timeout_outcome is None):
        raise InteractionError("deadline and timeout_outcome must be provided together.")
    if deadline is not None and deadline.tzinfo is None:
        raise InteractionError("deadline must be timezone-aware.")
    identifier = str(uuid.uuid4()) if request_id is None else _request_id(request_id)
    body = {"request_id": identifier, "prompt": prompt,
        "choices": [{"value": value, "label": label} for value, label in choices.items()],
        "deadline": (deadline.astimezone(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")  # noqa: UP017 -- package supports Python 3.10
                     if deadline is not None else None),
        "timeout_outcome": timeout_outcome}
    return _result(_call("POST", "/v1/runtime/interactions", body))


def get(request_id: str) -> Interaction:
    """Read the daemon-authoritative state for this execution's interaction."""
    return _result(_call("GET", f"/v1/runtime/interactions/{_request_id(request_id)}"))


def wait(request_id: str, *, poll_seconds: float = 30,
         on_timeout: Callable[[Interaction], None] | None = None) -> Interaction:
    """Wait until a human answers, the execution stops, or an explicit deadline expires.

    Interactions without a deadline remain pending indefinitely. If the caller
    explicitly configured a deadline, ``on_timeout`` runs once after the
    daemon records ``timed_out``; the returned interaction retains the
    daemon-authoritative timeout outcome.
    """
    query = urllib.parse.urlencode({"wait_seconds": poll_seconds})
    path = f"/v1/runtime/interactions/{_request_id(request_id)}/wait?{query}"
    while True:
        try:
            item = _result(_call("GET", path))
        except InteractionUnavailable:
            # The daemon can restart independently while the mission process
            # remains alive. Keep waiting and let its durable interaction state
            # resolve after connectivity returns.
            time.sleep(min(30.0, max(0.25, poll_seconds)))
            continue
        if item.state != "pending":
            if item.state == "timed_out" and on_timeout is not None:
                on_timeout(item)
            return item
