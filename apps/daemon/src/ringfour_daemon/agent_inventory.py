"""Read-only Agent hello and fresh capability checks; no SDK or ROS dependency."""

import asyncio
import json
from datetime import UTC, datetime
from urllib.parse import urlsplit

import websockets

from . import __version__
from .app_protocol import (
    AgentObservation,
    Platform,
    _unique_object,
    _validate_scalars,
)

MAX_WELCOME_BYTES = 1024 * 1024


class InventoryUnavailable(ValueError):
    def __init__(self, reason):
        super().__init__(reason)
        self.reason = reason


def utc_now():
    return datetime.now(UTC).isoformat(timespec="microseconds").replace("+00:00", "Z")


def engine_platform(settings):
    """Use declared engine facts, never os.uname() of an emulated Daemon."""
    if settings.apps_enabled:
        from .app_engine import Engine
        engine = Engine(settings)
        try:
            return engine.platform()
        finally:
            engine.close()
    if not settings.engine_os or not settings.engine_architecture:
        raise InventoryUnavailable("engine_platform_unconfigured")
    architecture = {"x86_64": "amd64", "aarch64": "arm64"}.get(
        settings.engine_architecture, settings.engine_architecture
    )
    return Platform(os=settings.engine_os, architecture=architecture,
                    native_facts=list(settings.engine_native_facts))


def welcome_observation(raw):
    try:
        if not isinstance(raw, str) or len(raw.encode("utf-8")) > MAX_WELCOME_BYTES:
            raise ValueError("invalid welcome frame")
        def bad_number(_):
            raise ValueError("invalid JSON number")
        value = json.loads(raw, object_pairs_hook=_unique_object, parse_constant=bad_number)
        _validate_scalars(value)
        if not isinstance(value, dict) or value.get("jsonrpc") != "2.0" or type(value.get("id")) is not int or value["id"] != 1:
            raise ValueError("unexpected RPC response")
        if "error" in value:
            error = value["error"]
            if isinstance(error, dict) and isinstance(error.get("data"), dict) and error["data"].get("type") == "ProtocolMismatch":
                raise InventoryUnavailable("unsupported_agent_protocol")
            raise ValueError("Agent hello refused")
        if set(value) != {"jsonrpc", "id", "result"}:
            raise ValueError("unexpected RPC response fields")
        welcome = value["result"]
        if not isinstance(welcome, dict) or type(welcome.get("protocol")) is not int:
            raise ValueError("missing protocol")
        if welcome["protocol"] != 1:
            raise InventoryUnavailable("unsupported_agent_protocol")
        profile = welcome["profile"]
        if profile["capabilities"] != welcome["capabilities"]:
            raise ValueError("inconsistent capabilities")
        return AgentObservation.model_validate({
            "observed_at": utc_now(),
            "source": {"kind": "agent_welcome", "protocol": 1,
                       "profile": {key: profile[key] for key in ("name", "model", "version")},
                       **({"agent_version": welcome["agent_version"]} if welcome.get("agent_version") else {})},
            "capabilities": welcome["capabilities"], "instances": welcome.get("instances", {}),
        })
    except InventoryUnavailable:
        raise
    except (ValueError, TypeError, KeyError, RecursionError) as error:
        raise InventoryUnavailable("handshake_invalid") from error


async def observe_agent(uri, timeout=3.0):
    """Get a new observation or raise; cached data never enters this function."""
    async def request():
        target = urlsplit(uri)
        if target.scheme == "unix" and not target.netloc and target.path.startswith("/"):
            connection = websockets.unix_connect(target.path, uri="ws://localhost/", max_size=MAX_WELCOME_BYTES)
        elif target.scheme in ("ws", "wss") and target.hostname and not target.username and not target.password:
            if target.scheme == "ws" and target.port is None:
                host = f"[{target.hostname}]" if ":" in target.hostname else target.hostname
                uri_with_port = target._replace(netloc=host + ":10101").geturl()
            else:
                uri_with_port = uri
            connection = websockets.connect(uri_with_port, max_size=MAX_WELCOME_BYTES)
        else:
            raise InventoryUnavailable("handshake_invalid")
        async with connection as socket:
            await socket.send(json.dumps({"jsonrpc": "2.0", "id": 1, "method": "hello",
                                          "params": {"protocol": 1, "client_version": "ringfour-daemon/" + __version__}}))
            return welcome_observation(await socket.recv())
    try:
        if timeout <= 0:
            raise ValueError("timeout must be positive")
        async with asyncio.timeout(timeout):
            return await request()
    except InventoryUnavailable:
        raise
    except (TimeoutError, OSError, websockets.WebSocketException) as error:
        raise InventoryUnavailable("agent_unreachable") from error
    except ValueError as error:
        raise InventoryUnavailable("handshake_invalid") from error


def evaluate(manifest, observation, platform):
    """Evaluate a validated v1 release against a freshly obtained observation."""
    def present(predicate):
        capability = predicate["capability"]
        return capability in observation.capabilities and (
            "instance_kind" not in predicate or bool(observation.instances.get("arms"))
        )
    missing = [p for p in manifest["capabilities"]["required"] if not present(p)]
    optional = [p for p in manifest["capabilities"]["optional"] if not present(p)]
    target = next((t for t in manifest["targets"]
                   if t["os"] == platform.os and t["architecture"] == platform.architecture), None)
    if target is None:
        return {"status": "no_matching_image", "checked_at": utc_now(), "inventory_observed_at": observation.observed_at,
                "target": None, "missing_required": [], "missing_optional": [], "missing_native": []}
    native = [] if target is None else [fact for fact in target["native_requirements"] if fact not in platform.native_facts]
    minimum_agent = manifest.get("toolchain", {}).get("agent", {}).get("min_version")
    reported_agent = observation.source.agent_version
    if minimum_agent and (
        not reported_agent or tuple(map(int, reported_agent.split("."))) < tuple(map(int, minimum_agent.split(".")))
    ):
        native.append(f"agent >= {minimum_agent} (reported {reported_agent or 'unknown'})")
    status = ("no_matching_image" if target is None else "missing_required" if missing else
              "native_requirement_unavailable" if native else "compatible")
    return {"status": status, "checked_at": utc_now(), "inventory_observed_at": observation.observed_at,
            "target": target, "missing_required": missing, "missing_optional": optional, "missing_native": native}


async def check_fresh(settings, manifest):
    """Install/start gate: caller must invoke before *any* engine side effect."""
    try:
        target = engine_platform(settings)
        observation = await observe_agent(settings.agent_uri, settings.agent_inventory_timeout_seconds)
        return evaluate(manifest, observation, target)
    except ValueError:
        return {"status": "inventory_unavailable", "checked_at": utc_now(), "inventory_observed_at": None,
                "target": None, "missing_required": [], "missing_optional": [], "missing_native": []}
