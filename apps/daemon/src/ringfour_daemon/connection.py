import asyncio
import json
import logging
import random
import time
from collections.abc import Callable, Coroutine
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

import websockets

from . import __version__, operations
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
        execute: Callable[[operations.DeviceOperation], dict] | None = None,
        sleep: Callable[[float], Coroutine[Any, Any, None]] = asyncio.sleep,
        ws_connect: Callable[..., Any] = websockets.connect,
        now: Callable[[], datetime] = lambda: datetime.now(UTC),
        uuid: Callable[[], str] | None = None,
    ) -> None:
        self._settings = settings
        self._execute = execute or (lambda operation: {"noop": True})
        self._sleep = sleep
        self._ws_connect = ws_connect
        self._now = now
        self._uuid = uuid or (lambda: f"{time.monotonic_ns():x}")
        self._stopped = False
        # Total effect executions across reconnects; exposed for tests.
        self.effects = 0

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
            logger.info("reconnecting in %.1fs (attempt %d)", delay, attempt)
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
                tasks.append(
                    asyncio.create_task(
                        self._reconcile_later(replayed_ids)
                    )
                )
                tasks.append(
                    asyncio.create_task(self._heartbeat_loop(outbound, state, heartbeat_seconds))
                )
                await self._receive_loop(socket, outbound, state, replayed_ids)
            finally:
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
        replayed_ids.add(command["operation_id"])
        try:
            operation, created = operations.record_command(
                self._settings.db_path, state.robot_id, command
            )
        except operations.OperationConflict:
            logger.error("conflicting reuse of operation %s", command["operation_id"])
            raise ProtocolError("conflicting operation id") from None

        # The local record is committed before this ack goes out.
        await self._queue(outbound, state, "ack", {"operation_id": operation.operation_id})
        if not created:
            # Replay of a command we already hold: re-report stored status,
            # never execute the effect again.
            if operation.state in operations.TERMINAL_STATES:
                await self._report(outbound, state, operation)
            return

        if operations.is_expired(operation.deadline_at, self._now()):
            finished = operations.apply_status(
                self._settings.db_path,
                operation.operation_id,
                "failed",
                {
                    "error": {
                        "code": "deadline_exceeded",
                        "message": "The operation deadline passed before execution.",
                    }
                },
            )
            await self._report(outbound, state, finished)
            return

        operations.apply_status(self._settings.db_path, operation.operation_id, "running")
        try:
            result = self._execute(operation)
            self.effects += 1
            finished = operations.apply_status(
                self._settings.db_path, operation.operation_id, "succeeded", result
            )
        except Exception:
            logger.exception("operation %s failed", operation.operation_id)
            finished = operations.apply_status(
                self._settings.db_path,
                operation.operation_id,
                "failed",
                {
                    "error": {
                        "code": "execution_failed",
                        "message": "The operation raised an exception.",
                    }
                },
            )
        await self._report(outbound, state, finished)

    def _handle_error(self, error: dict) -> None:
        code = error.get("code")
        logger.warning("platform reported protocol error %s: %s", code, error.get("message"))
        if code == "session_replaced":
            raise ProtocolError("session replaced by a newer connection")
        if code in ("stale_sequence", "sequence_gap", "malformed_message"):
            raise ProtocolError(f"protocol violation: {code}")
        # unknown_operation / state_conflict are per-operation; the platform
        # stays authoritative and reconciles on the next replay.

    async def _reconcile_later(self, replayed_ids: set[str]) -> None:
        # The platform replays every nonterminal operation right after
        # welcome, so any local nonterminal record absent from this session's
        # replay is terminal-or-gone upstream.
        await self._sleep(self._settings.reconcile_delay_seconds)
        for operation in operations.list_nonterminal(self._settings.db_path):
            if operation.operation_id in replayed_ids:
                continue
            logger.info(
                "operation %s no longer active on the platform; marking superseded",
                operation.operation_id,
            )
            operations.mark_superseded(self._settings.db_path, operation.operation_id)

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

    async def _report(
        self, outbound: asyncio.Queue, state: _SessionState, operation: operations.DeviceOperation
    ) -> None:
        payload: dict = {
            "operation_id": operation.operation_id,
            "state": operation.state,
        }
        if operation.result is not None:
            payload["result"] = operation.result
        await self._queue(outbound, state, "operation_status", payload)
