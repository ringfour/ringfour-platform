import asyncio
import contextlib
import json
import os
import sqlite3
from datetime import UTC, datetime, timedelta

import websockets

from ringfour_daemon import operations
from ringfour_daemon.config import Settings
from ringfour_daemon.connection import (
    ConnectionManager,
    backoff_with_jitter,
    websocket_url,
)
from ringfour_daemon.db import migrate

ROBOT_ID = "7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e"
CREDENTIAL_ID = "3c4d5e6f-7a8b-4c9d-0e1f-2a3b4c5d6e7f"


def deadline_in(seconds: float) -> str:
    return (datetime.now(UTC) + timedelta(seconds=seconds)).isoformat(
        timespec="microseconds"
    )


def command_payload(operation_id: str, *, deadline: str | None = None, payload=None) -> dict:
    return {
        "operation_id": operation_id,
        "kind": "diagnostic.noop",
        "payload_version": 1,
        "payload": payload if payload is not None else {},
        "deadline_at": deadline or deadline_in(60),
    }


class StubPlatform:
    """Speaks the real WebSocket protocol on localhost so the manager under
    test does real I/O; only the platform's behavior is scripted."""

    def __init__(self) -> None:
        self.received: asyncio.Queue = asyncio.Queue()
        self.connections = 0
        self.current = None
        self._sequence = 0
        self._server = None

    async def start(self) -> str:
        self._server = await websockets.serve(self._handler, "127.0.0.1", 0)
        port = self._server.sockets[0].getsockname()[1]
        return f"ws://127.0.0.1:{port}"

    async def stop(self) -> None:
        self._server.close()
        await self._server.wait_closed()

    async def _handler(self, socket) -> None:
        self.connections += 1
        self.current = socket
        self._sequence = 0
        try:
            async for raw in socket:
                await self.received.put(json.loads(raw))
        except websockets.ConnectionClosed:
            pass

    async def send(self, kind: str, payload: dict) -> None:
        self._sequence += 1
        envelope = {
            "version": 1,
            "message_id": f"stub-{self._sequence}",
            "type": kind,
            "robot_id": ROBOT_ID,
            "sequence": self._sequence,
            "payload": payload,
        }
        await self.current.send(json.dumps(envelope))

    async def expect_hello(self) -> dict:
        hello = await asyncio.wait_for(self.received.get(), timeout=5)
        assert hello["type"] == "hello"
        assert hello["sequence"] == 1
        assert hello["payload"]["supported_protocol_versions"] == [1]
        await self.send(
            "welcome",
            {
                "server_time": deadline_in(0),
                "heartbeat_interval_seconds": 1,
                "session_id": "8d3e1f2a-9b4c-4d5e-8f6a-7b8c9d0e1f2a",
            },
        )
        return hello


class FastClock:
    """sleep() that records the delay and returns immediately; stops the
    manager after a configurable number of calls so run() can finish."""

    def __init__(self, stop_after: int | None = None) -> None:
        self.delays: list[float] = []
        self._stop_after = stop_after

    def __call__(self, delay: float):
        self.delays.append(delay)
        return asyncio.sleep(0)


def make_settings(tmp_path, platform_url: str, **overrides) -> Settings:
    db_path = str(tmp_path / "daemon.db")
    credential_path = str(tmp_path / "robot-credential")
    migrate(db_path)
    with open(credential_path, "w") as handle:
        handle.write("rfs_test-secret")
    os.chmod(credential_path, 0o600)
    with sqlite3.connect(db_path) as conn:
        conn.execute(
            "INSERT INTO device_identity"
            " (singleton_id, robot_id, organization_id, platform_url, credential_id)"
            " VALUES (1, ?, ?, ?, ?)",
            (ROBOT_ID, "9b8a7c6d-5e4f-4a3b-8c7d-6e5f4a3b2c1d", platform_url, CREDENTIAL_ID),
        )
    values = {
        "db_path": db_path,
        "runner_url": "http://localhost:8082",
        "platform_url": platform_url,
        "credential_path": credential_path,
        "backoff_initial_seconds": 1.0,
        "backoff_max_seconds": 30.0,
        "reconcile_delay_seconds": 20.0,
        "outbound_queue_limit": 128,
    }
    values.update(overrides)
    return Settings(**values)


async def run_manager(manager: ConnectionManager, stop_when_done) -> None:
    task = asyncio.create_task(manager.run())
    try:
        await stop_when_done
    finally:
        manager.stop()
        task.cancel()
        with contextlib.suppress(asyncio.CancelledError, Exception):
            await task


def run(coro):
    return asyncio.run(coro)


def test_backoff_grows_exponentially_caps_and_jitters():
    assert backoff_with_jitter(0, rand=lambda: 0.5) == 1.0
    assert backoff_with_jitter(1, rand=lambda: 0.5) == 2.0
    assert backoff_with_jitter(3, rand=lambda: 0.5) == 8.0
    assert backoff_with_jitter(100, rand=lambda: 0.5) == 30.0
    # Jitter stays within +/- 20% of the base.
    low = backoff_with_jitter(2, rand=lambda: 0.0)
    high = backoff_with_jitter(2, rand=lambda: 1.0)
    assert 3.2 <= low <= 4.0
    assert 4.0 <= high <= 4.8 + 1e-9


def test_websocket_url_derivation():
    assert websocket_url("http://api:8080") == "ws://api:8080/v1/device/connect"
    assert websocket_url("https://api.ringfour.eu/") == "wss://api.ringfour.eu/v1/device/connect"


def test_full_session_executes_and_reports_terminal_status(tmp_path):
    async def scenario():
        stub = StubPlatform()
        url = await stub.start()
        try:
            settings = make_settings(tmp_path, url)
            effects: list[str] = []
            manager = ConnectionManager(
                settings,
                execute=lambda operation: effects.append(operation.operation_id) or {"noop": True},
                uuid=lambda: "fixed",
            )
            done = asyncio.Event()

            async def drive():
                await stub.expect_hello()
                await stub.send(
                    "command", command_payload("4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b")
                )
                ack = await asyncio.wait_for(stub.received.get(), timeout=5)
                assert ack["type"] == "ack"
                assert ack["sequence"] == 2
                status = await asyncio.wait_for(stub.received.get(), timeout=5)
                assert status["type"] == "operation_status"
                assert status["payload"]["state"] == "succeeded"
                assert status["payload"]["result"] == {"noop": True}
                done.set()

            await asyncio.gather(run_manager(manager, done.wait()), drive())
            assert manager.effects == 1
            assert effects == ["4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b"]
            stored = operations.get(settings.db_path, "4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b")
            assert stored is not None
            assert stored.state == "succeeded"
            assert stored.result == {"noop": True}
        finally:
            await stub.stop()

    run(scenario())


def test_replayed_command_does_not_reexecute_after_restart(tmp_path):
    """The platform replays nonterminal operations after a daemon restart; a
    command already committed locally must replay its status, not its effect."""

    async def scenario():
        stub = StubPlatform()
        url = await stub.start()
        try:
            settings = make_settings(tmp_path, url)
            command = command_payload("4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b")
            # Simulate the pre-restart daemon: the command was committed, then
            # the daemon died before the platform saw any status.
            operations.record_command(settings.db_path, ROBOT_ID, command)

            effects: list[str] = []
            manager = ConnectionManager(
                settings, execute=lambda op: effects.append(op.operation_id) or {"noop": True}
            )
            done = asyncio.Event()

            async def drive():
                await stub.expect_hello()
                # Platform replay of the still-accepted operation.
                await stub.send("command", command)
                replayed = await asyncio.wait_for(stub.received.get(), timeout=5)
                assert replayed["type"] == "ack"
                # The stored record is still accepted, so no terminal replay.
                done.set()

            await asyncio.gather(run_manager(manager, done.wait()), drive())
            assert effects == []
            assert manager.effects == 0
            stored = operations.get(settings.db_path, command["operation_id"])
            assert stored is not None
            assert stored.state == "accepted"
        finally:
            await stub.stop()

    run(scenario())


def test_terminal_replay_after_restart_reports_terminal_status(tmp_path):
    async def scenario():
        stub = StubPlatform()
        url = await stub.start()
        try:
            settings = make_settings(tmp_path, url)
            command = command_payload("4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b")
            operations.record_command(settings.db_path, ROBOT_ID, command)
            operations.apply_status(
                settings.db_path, command["operation_id"], "succeeded", {"noop": True}
            )

            manager = ConnectionManager(settings)
            done = asyncio.Event()

            async def drive():
                await stub.expect_hello()
                await stub.send("command", command)
                ack = await asyncio.wait_for(stub.received.get(), timeout=5)
                assert ack["type"] == "ack"
                status = await asyncio.wait_for(stub.received.get(), timeout=5)
                assert status["payload"]["state"] == "succeeded"
                done.set()

            await asyncio.gather(run_manager(manager, done.wait()), drive())
            assert manager.effects == 0
        finally:
            await stub.stop()

    run(scenario())


def test_conflicting_operation_id_disconnects_and_reconnects(tmp_path):
    async def scenario():
        stub = StubPlatform()
        url = await stub.start()
        try:
            settings = make_settings(tmp_path, url, backoff_initial_seconds=0.01)
            clock = FastClock()
            manager = ConnectionManager(settings, sleep=clock)
            seen_two_sessions = asyncio.Event()

            async def drive():
                await stub.expect_hello()
                operation_id = "4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b"
                await stub.send("command", command_payload(operation_id))
                ack = await asyncio.wait_for(stub.received.get(), timeout=5)
                assert ack["type"] == "ack"
                # Conflicting reuse of the same operation id.
                await stub.send(
                    "command", command_payload(operation_id, payload={"different": True})
                )
                await asyncio.wait_for(seen_two_sessions.wait(), timeout=5)

            async def count_connections():
                while stub.connections < 2:
                    await asyncio.sleep(0.01)
                seen_two_sessions.set()

            await asyncio.gather(
                run_manager(manager, seen_two_sessions.wait()),
                drive(),
                count_connections(),
            )
            assert stub.connections >= 2
            assert clock.delays, "reconnect must wait out a backoff delay"
        finally:
            await stub.stop()

    run(scenario())


def test_rejected_session_retries_with_backoff(tmp_path):
    async def scenario():
        stub = StubPlatform()
        url = await stub.start()
        try:
            settings = make_settings(tmp_path, url, backoff_initial_seconds=1.0)
            clock = FastClock()
            manager = ConnectionManager(settings, sleep=clock)
            third_attempt = asyncio.Event()

            async def drive():
                # Every connection is dropped right after hello, like a revoked
                # credential denied at the platform.
                for _ in range(3):
                    hello = await asyncio.wait_for(stub.received.get(), timeout=5)
                    assert hello["type"] == "hello"
                    await stub.current.close(code=4401, reason="credential_revoked")
                third_attempt.set()

            await asyncio.gather(run_manager(manager, third_attempt.wait()), drive())
            assert stub.connections == 3
            assert len(clock.delays) >= 2
            # Backoff doubles per attempt within the +/- jitter band.
            assert 0.8 <= clock.delays[0] <= 1.2
            assert 1.6 <= clock.delays[1] <= 2.4
        finally:
            await stub.stop()

    run(scenario())


def test_expired_deadline_is_not_executed(tmp_path):
    async def scenario():
        stub = StubPlatform()
        url = await stub.start()
        try:
            settings = make_settings(tmp_path, url)
            manager = ConnectionManager(settings)
            done = asyncio.Event()

            async def drive():
                await stub.expect_hello()
                await stub.send(
                    "command",
                    command_payload(
                        "4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b", deadline=deadline_in(-5)
                    ),
                )
                ack = await asyncio.wait_for(stub.received.get(), timeout=5)
                assert ack["type"] == "ack"
                status = await asyncio.wait_for(stub.received.get(), timeout=5)
                assert status["payload"]["state"] == "failed"
                assert status["payload"]["result"]["error"]["code"] == "deadline_exceeded"
                done.set()

            await asyncio.gather(run_manager(manager, done.wait()), drive())
            assert manager.effects == 0
            stored = operations.get(settings.db_path, "4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b")
            assert stored is not None
            assert stored.state == "failed"
            assert stored.result["error"]["code"] == "deadline_exceeded"
        finally:
            await stub.stop()

    run(scenario())


def test_reconciliation_marks_unreplay_operations_superseded(tmp_path):
    async def scenario():
        stub = StubPlatform()
        url = await stub.start()
        try:
            settings = make_settings(tmp_path, url, reconcile_delay_seconds=0.05)
            # A nonterminal record the platform does not replay (e.g. it went
            # terminal while the daemon was away).
            ghost = command_payload("4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b")
            operations.record_command(settings.db_path, ROBOT_ID, ghost)

            manager = ConnectionManager(settings)
            done = asyncio.Event()

            async def drive():
                await stub.expect_hello()
                for _ in range(20):
                    await asyncio.sleep(0.05)
                    stored = operations.get(settings.db_path, ghost["operation_id"])
                    if stored is not None and stored.state == "failed":
                        done.set()
                        return
                raise AssertionError("ghost operation was not reconciled")

            await asyncio.gather(run_manager(manager, done.wait()), drive())
        finally:
            await stub.stop()

    run(scenario())


def test_heartbeat_uses_the_negotiated_interval(tmp_path):
    async def scenario():
        stub = StubPlatform()
        url = await stub.start()
        try:
            settings = make_settings(tmp_path, url)
            manager = ConnectionManager(settings)
            done = asyncio.Event()

            async def drive():
                hello = await asyncio.wait_for(stub.received.get(), timeout=5)
                assert hello["type"] == "hello"
                await stub.send(
                    "welcome",
                    {
                        "server_time": deadline_in(0),
                        "heartbeat_interval_seconds": 1,
                        "session_id": "8d3e1f2a-9b4c-4d5e-8f6a-7b8c9d0e1f2a",
                    },
                )
                beat = await asyncio.wait_for(stub.received.get(), timeout=5)
                assert beat["type"] == "heartbeat"
                assert beat["payload"] == {}
                done.set()

            await asyncio.gather(run_manager(manager, done.wait()), drive())
        finally:
            await stub.stop()

    run(scenario())
