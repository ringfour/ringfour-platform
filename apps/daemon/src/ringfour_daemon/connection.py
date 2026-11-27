import asyncio
import json
import logging
import random
import sqlite3
import time
from collections.abc import Callable, Coroutine
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

import httpx
import websockets

from . import (
    __version__,
    app_installations,
    app_inventory,
    operations,
    runtime_settings,
)
from .enrollment import credential_error, load_identity

logger = logging.getLogger(__name__)

PROTOCOL_VERSION = 1
_ENROLLMENT_POLL_SECONDS = 5.0


def backoff_with_jitter(
    attempt: int,
    initial: float = 1.0,
    maximum: float = 30.0,
    jitter: float = 0.2,
    rand: Callable[[], float] = random.random,
) -> float:
    """Exponential backoff with a +/- jitter fraction, capped at maximum."""
    base = min(initial * (2**attempt), maximum)
    spread = base * jitter
    return max(0.0, base - spread + rand() * 2 * spread)


def websocket_url(platform_url: str) -> str:
    return (
        platform_url.replace("http://", "ws://", 1)
        .replace("https://", "wss://", 1)
        .rstrip("/")
        + "/v1/device/connect"
    )


class ProtocolError(Exception):
    """A protocol violation or lost-session condition: reconnect."""


@dataclass
class _SessionState:
    robot_id: str
    session_id: str | None = None
    outbound_sequence: int = 1  # hello consumes sequence 1
    inbound_sequence: int = 0  # welcome must carry sequence 1

    def next_outbound(self) -> int:
        self.outbound_sequence += 1
        return self.outbound_sequence

    def next_inbound(self) -> int:
        self.inbound_sequence += 1
        return self.inbound_sequence


class ConnectionManager:
    """Owns the outbound WebSocket to the platform: reconnect with backoff,
    heartbeat, command deduplication against the SQLite store, and status
    reporting. Durable state lives in the store, so restarts converge through
    server-side replay of nonterminal operations."""

    def __init__(
        self,
        settings,
        *,
        sleep: Callable[[float], Coroutine[Any, Any, None]] = asyncio.sleep,
        ws_connect: Callable[..., Any] = websockets.connect,
        now: Callable[[], datetime] = lambda: datetime.now(UTC),
        uuid: Callable[[], str] | None = None,
    ) -> None:
        self._settings = settings
        self._sleep = sleep
        self._ws_connect = ws_connect
        self._now = now
        self._uuid = uuid or (lambda: f"{time.monotonic_ns():x}")
        self._stopped = False
        self.connected = False
        self.app_executor = None
        self.app_work = asyncio.Event()

    def stop(self) -> None:
        self._stopped = True

    async def run(self) -> None:
        # Enrollment can happen at any time through the daemon's HTTP API
        # (long after boot), so presence is polled rather than checked once.
        while not self._stopped:
            identity = load_identity(self._settings.db_path)
            if identity is None or credential_error(self._settings.credential_path):
                logger.info("device is not enrolled yet; waiting")
                await self._sleep(_ENROLLMENT_POLL_SECONDS)
                continue
            if self._settings.apps_enabled and self.app_executor is None:
                from .app_engine import Executor
                self.app_executor = Executor(self._settings).open()
            await self._run_connected(identity)

    async def _run_connected(self, identity: dict) -> None:
        attempt = 0
        while not self._stopped:
            try:
                await self._session(identity)
                attempt = 0
            except asyncio.CancelledError:
                raise
            except Exception:
                logger.exception("device session failed; reconnecting with backoff")
            if self._stopped:
                break
            delay = backoff_with_jitter(
                attempt,
                initial=self._settings.backoff_initial_seconds,
                maximum=self._settings.backoff_max_seconds,
            )
            attempt += 1
            logger.info(
                "robot %s reconnecting in %.1fs (attempt %d)",
                identity["robot_id"], delay, attempt,
            )
            await self._sleep(delay)

    async def _session(self, identity: dict) -> None:
        secret = Path(self._settings.credential_path).read_text().strip()
        url = websocket_url(identity["platform_url"])
        headers = {
            "x-ringfour-credential-id": identity["credential_id"],
            "x-ringfour-credential-secret": secret,
        }
        state = _SessionState(robot_id=identity["robot_id"])
        replayed_ids: set[str] = set()
        async with self._ws_connect(url, additional_headers=headers) as socket:
            outbound: asyncio.Queue[str] = asyncio.Queue(
                maxsize=self._settings.outbound_queue_limit
            )
            sender = asyncio.create_task(self._send_loop(socket, outbound))
            tasks: list[asyncio.Task] = [sender]
            try:
                await self._say_hello(outbound, state)
                heartbeat_seconds = await self._await_welcome(socket, state)
                self.connected = True
                self.app_headers = headers | {"x-ringfour-device-session": state.session_id}
                if self._settings.agent_uri:
                    tasks.append(asyncio.create_task(app_inventory.run_session(
                        self._settings, identity, state.session_id, headers)))
                tasks.append(
                    asyncio.create_task(self._heartbeat_loop(outbound, state, heartbeat_seconds))
                )
                await self._receive_loop(socket, outbound, state, replayed_ids)
            finally:
                self.connected = False
                for task in tasks:
                    task.cancel()

    # -- protocol steps -----------------------------------------------------

    def _envelope(
        self, state: _SessionState, kind: str, payload: dict, sequence: int | None = None
    ) -> str:
        return json.dumps(
            {
                "version": PROTOCOL_VERSION,
                "message_id": f"daemon-{self._uuid()}",
                "type": kind,
                "robot_id": state.robot_id,
                "sequence": sequence if sequence is not None else state.next_outbound(),
                "payload": payload,
            }
        )

    async def _say_hello(self, outbound: asyncio.Queue, state: _SessionState) -> None:
        await outbound.put(
            self._envelope(
                state,
                "hello",
                {
                    "daemon_version": __version__,
                    "supported_protocol_versions": [PROTOCOL_VERSION],
                },
                sequence=1,
            )
        )

    async def _await_welcome(self, socket, state: _SessionState) -> int:
        envelope = await self._read_envelope(socket, state)
        if envelope["type"] != "welcome":
            raise ProtocolError(f"expected welcome, got {envelope['type']}")
        payload = envelope["payload"]
        heartbeat = int(payload["heartbeat_interval_seconds"])
        state.session_id = payload["session_id"]
        logger.info(
            "connected to platform as robot %s (session %s, heartbeat %ds)",
            state.robot_id,
            payload.get("session_id"),
            heartbeat,
        )
        return heartbeat

    async def _receive_loop(
        self,
        socket,
        outbound: asyncio.Queue,
        state: _SessionState,
        replayed_ids: set[str],
    ) -> None:
        async for raw in socket:
            envelope = self._validate_envelope(raw, state)
            if envelope["sequence"] != state.next_inbound():
                raise ProtocolError("stale or gapped server sequence")
            kind = envelope["type"]
            if kind == "command":
                await self._handle_command(envelope["payload"], outbound, state, replayed_ids)
            elif kind == "error":
                self._handle_error(envelope["payload"])
            elif kind == "heartbeat":
                continue
            else:
                raise ProtocolError(f"unexpected message type {kind}")

    async def _handle_command(
        self,
        command: dict,
        outbound: asyncio.Queue,
        state: _SessionState,
        replayed_ids: set[str],
    ) -> None:
        if command.get("kind") in ("app.start", "app.stop"):
            if not self._settings.apps_enabled or self.app_executor is None:
                raise ProtocolError("App run consumer is not enabled")
            definition = "start_command" if command["kind"] == "app.start" else "stop_command"
            app_installations.app_validation.validate(command["payload"], "device", definition)
            operation, _ = operations.record_command(self._settings.db_path, state.robot_id, command)
            replayed_ids.add(operation.operation_id)
            await self._queue(outbound, state, "ack", {"operation_id": operation.operation_id})
            self.app_work.set()
            return
        if command.get("kind") == "app.uninstall":
            if not self._settings.apps_enabled or self.app_executor is None:
                raise ProtocolError("App uninstall consumer is not enabled")
            app_installations.app_validation.validate(command["payload"], "device", "uninstall_command")
            operation, _ = operations.record_command(self._settings.db_path, state.robot_id, command)
            replayed_ids.add(operation.operation_id)
            await self._queue(outbound, state, "ack", {"operation_id": operation.operation_id})
            try:
                await app_installations.process_uninstall(self._settings, self.app_executor, operation, self.app_headers)
            except (httpx.HTTPError, app_installations.PreparationError, sqlite3.Error, ValueError):
                raise ProtocolError("App uninstall/report delivery unavailable; reconcile on reconnect") from None
            return
        if command.get("kind", "").startswith("app."):
            if command["kind"] == "app.settings.apply":
                if not self._settings.apps_enabled or self.app_executor is None:
                    raise ProtocolError("App settings consumer is not enabled")
                app_installations.app_validation.validate(command["payload"], "device", "settings_apply_command")
                operation, _ = operations.record_command(self._settings.db_path, state.robot_id, command)
                try:
                    runtime_settings.accept_command(self._settings.db_path, state.robot_id, command)
                except (sqlite3.Error, ValueError):
                    raise ProtocolError("App settings command could not be durably recorded") from None
                replayed_ids.add(operation.operation_id)
                await self._queue(outbound, state, "ack", {"operation_id": operation.operation_id})
                self.app_work.set()
                return
            if command["kind"] not in ("app.install", "app.configure", "app.update", "app.rollback") or not self._settings.apps_enabled or self.app_executor is None:
                raise ProtocolError("App installation consumer is not enabled")
            app_installations.app_validation.validate(command["payload"], "device", "apply_command")
            operation, _ = operations.record_command(self._settings.db_path, state.robot_id, command)
            replayed_ids.add(operation.operation_id)
            await self._queue(outbound, state, "ack", {"operation_id": operation.operation_id})
            try:
                await app_installations.process(self._settings, self.app_executor, operation, self.app_headers)
            except (httpx.HTTPError, app_installations.PreparationError, sqlite3.Error, ValueError):
                raise ProtocolError("App preparation/report delivery unavailable; reconcile on reconnect") from None
            return
        raise ProtocolError("Unsupported command kind; only App commands are accepted")

    def _handle_error(self, error: dict) -> None:
        code = error.get("code")
        logger.warning("platform reported protocol error %s: %s", code, error.get("message"))
        if code == "session_replaced":
            raise ProtocolError("session replaced by a newer connection")
        if code in ("stale_sequence", "sequence_gap", "malformed_message"):
            raise ProtocolError(f"protocol violation: {code}")
        # unknown_operation / state_conflict are per-operation; the platform
        # stays authoritative and reconciles on the next replay.

    async def _heartbeat_loop(
        self, outbound: asyncio.Queue, state: _SessionState, heartbeat_seconds: int
    ) -> None:
        while True:
            await self._sleep(float(heartbeat_seconds))
            await self._queue(outbound, state, "heartbeat", {})

    async def _send_loop(self, socket, outbound: asyncio.Queue) -> None:
        while True:
            message = await outbound.get()
            await socket.send(message)

    # -- helpers ------------------------------------------------------------

    def _validate_envelope(self, raw: str, state: _SessionState) -> dict:
        try:
            envelope = json.loads(raw)
        except ValueError as error:
            raise ProtocolError("malformed envelope") from error
        if (
            envelope.get("version") != PROTOCOL_VERSION
            or envelope.get("robot_id") != state.robot_id
            or not isinstance(envelope.get("payload"), dict)
        ):
            raise ProtocolError("malformed envelope")
        return envelope

    async def _read_envelope(self, socket, state: _SessionState) -> dict:
        raw = await socket.recv()
        envelope = self._validate_envelope(raw, state)
        if envelope["sequence"] != state.next_inbound():
            raise ProtocolError("unexpected server sequence")
        return envelope

    async def _queue(
        self, outbound: asyncio.Queue, state: _SessionState, kind: str, payload: dict
    ) -> None:
        try:
            outbound.put_nowait(self._envelope(state, kind, payload))
        except asyncio.QueueFull:
            # The platform replays unacknowledged work after reconnect; a full
            # outbound queue is a backpressure signal, not data loss.
            raise ProtocolError("outbound queue full; reconnecting") from None
