"""Dedicated outbound UI/status tunnel; never shares the command socket."""
import asyncio
import json
import logging
import uuid
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

import httpx
import websockets

from .enrollment import credential_error, load_identity

logger = logging.getLogger(__name__)
MAX_FRAME_BYTES = 64 * 1024


def websocket_url(platform_url: str) -> str:
    return (platform_url.replace("http://", "ws://", 1)
            .replace("https://", "wss://", 1).rstrip("/") + "/v1/device/ui-connect")


class MissionUiConnectionManager:
    def __init__(self, settings, *, ws_connect=websockets.connect, sleep=asyncio.sleep):
        self._settings = settings
        self._ws_connect = ws_connect
        self._sleep = sleep
        self._stopped = False
        self.connected = False
        self._daemon_instance_id = str(uuid.uuid4())

    def stop(self):
        self._stopped = True

    async def run(self):
        attempt = 0
        while not self._stopped:
            identity = load_identity(self._settings.db_path)
            if identity is None or credential_error(self._settings.credential_path) or not self._settings.runner_auth_token:
                await self._sleep(5)
                continue
            try:
                await self._session(identity)
                attempt = 0
            except asyncio.CancelledError:
                raise
            except Exception:
                logger.exception("mission UI tunnel failed; reconnecting")
            if not self._stopped:
                from .connection import backoff_with_jitter
                await self._sleep(backoff_with_jitter(
                    attempt,
                    initial=self._settings.backoff_initial_seconds,
                    maximum=self._settings.backoff_max_seconds,
                ))
                attempt += 1

    async def _snapshot(self):
        async with httpx.AsyncClient(
            base_url=self._settings.runner_url,
            headers={"authorization": f"Bearer {self._settings.runner_auth_token}"},
            timeout=3,
        ) as client:
            response = await client.get("/v1/ui")
            response.raise_for_status()
            body = response.json()
        executions = body.get("executions") if isinstance(body, dict) else None
        if not isinstance(executions, list) or len(executions) > 64:
            raise ValueError("Runner returned an invalid UI status list")
        result = {}
        for item in executions:
            if (not isinstance(item, dict) or set(item) != {"execution_id", "state"}
                    or item["state"] not in {"starting", "ready", "unavailable", "ended"}):
                raise ValueError("Runner returned an invalid UI status")
            result[item["execution_id"]] = item["state"]
        return result

    async def _session(self, identity):
        headers = {
            "x-ringfour-credential-id": identity["credential_id"],
            "x-ringfour-credential-secret": Path(self._settings.credential_path).read_text().strip(),
        }
        states = await self._snapshot()
        lock = asyncio.Lock()
        sequence = 0

        async def send(socket, kind: str, payload: dict[str, Any]):
            nonlocal sequence
            async with lock:
                sequence += 1
                envelope = {
                    "protocol_version": 1,
                    "message_id": str(uuid.uuid4()),
                    "sequence": sequence,
                    "type": kind,
                    "payload": payload,
                }
                raw = json.dumps(envelope, separators=(",", ":"))
                if len(raw.encode()) > MAX_FRAME_BYTES:
                    raise ValueError("mission UI tunnel frame exceeds its limit")
                await socket.send(raw)

        self.connected = False
        async with self._ws_connect(websocket_url(identity["platform_url"]), additional_headers=headers,
                                    max_size=MAX_FRAME_BYTES) as socket:
            await send(socket, "hello", {
                "daemon_instance_id": self._daemon_instance_id,
                "executions": [{"execution_id": key, "state": value} for key, value in states.items()],
            })
            raw = await asyncio.wait_for(socket.recv(), timeout=10)
            welcome = json.loads(raw)
            if (not isinstance(welcome, dict) or welcome.get("protocol_version") != 1
                    or welcome.get("type") != "welcome" or welcome.get("sequence") != 1):
                raise ValueError("Platform returned an invalid UI tunnel welcome")
            self.connected = True
            logger.info("mission UI tunnel connected for robot %s", identity["robot_id"])

            async def report_loop():
                previous = dict(states)
                last_heartbeat = 0.0
                while True:
                    current = await self._snapshot()
                    for execution_id, state in current.items():
                        if previous.get(execution_id) != state:
                            await send(socket, "ui_status", {"execution_id": execution_id, "state": state})
                    for execution_id in previous.keys() - current.keys():
                        await send(socket, "ui_status", {"execution_id": execution_id, "state": "ended"})
                    previous = current
                    now = asyncio.get_running_loop().time()
                    if now - last_heartbeat >= 15:
                        await send(socket, "heartbeat", {"sent_at": datetime.now(UTC).isoformat().replace("+00:00", "Z")})
                        last_heartbeat = now
                    await self._sleep(2)

            async def read_loop():
                expected = 2
                async for raw_frame in socket:
                    frame = json.loads(raw_frame)
                    if (not isinstance(frame, dict) or frame.get("protocol_version") != 1
                            or frame.get("sequence") != expected or frame.get("type") != "heartbeat"):
                        raise ValueError("Platform sent an invalid UI tunnel frame")
                    expected += 1
                raise ConnectionError("Platform closed the UI tunnel")

            reporter = asyncio.create_task(report_loop())
            reader = asyncio.create_task(read_loop())
            self.connected = True
            try:
                done, pending = await asyncio.wait((reporter, reader), return_when=asyncio.FIRST_COMPLETED)
                for task in pending:
                    task.cancel()
                for task in done:
                    task.result()
            finally:
                self.connected = False
                reporter.cancel()
                reader.cancel()
                await asyncio.gather(reporter, reader, return_exceptions=True)
