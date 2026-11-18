import asyncio
import json
from types import SimpleNamespace

import pytest
import websockets

from ringfour_daemon.mission_ui_connection import MissionUiConnectionManager, websocket_url


EXECUTION = "4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b"


def test_ui_websocket_url_is_a_separate_endpoint():
    assert websocket_url("http://api:8080") == "ws://api:8080/v1/device/ui-connect"
    assert websocket_url("https://api.ringfour.eu/") == "wss://api.ringfour.eu/v1/device/ui-connect"


def test_ui_channel_reports_runner_state_on_separate_socket(tmp_path):
    async def scenario():
        credential = tmp_path / "credential"
        credential.write_text("credential-secret")
        settings = SimpleNamespace(
        db_path=str(tmp_path / "daemon.db"),
        credential_path=str(credential),
        runner_auth_token="runner-secret",
        runner_url="http://runner",
        backoff_initial_seconds=0,
        backoff_max_seconds=1,
    )
        received = []

        async def handler(socket):
            hello = json.loads(await socket.recv())
            received.append(hello)
            await socket.send(json.dumps({
            "protocol_version": 1,
            "message_id": "11111111-1111-4111-8111-111111111111",
            "sequence": 1,
            "type": "welcome",
            "payload": {
                "connection_id": "22222222-2222-4222-8222-222222222222",
                "heartbeat_interval_seconds": 30,
            },
            }))
            received.append(json.loads(await socket.recv()))
            received.append(json.loads(await socket.recv()))
            await socket.close()

        async with websockets.serve(handler, "127.0.0.1", 0) as server:
            port = server.sockets[0].getsockname()[1]
            manager = MissionUiConnectionManager(settings, sleep=lambda _: asyncio.sleep(0))
            snapshots = iter(({EXECUTION: "starting"}, {EXECUTION: "ready"}))
            latest = {EXECUTION: "starting"}

            async def snapshot():
                nonlocal latest
                latest = next(snapshots, latest)
                return latest

            manager._snapshot = snapshot
            with pytest.raises(ConnectionError, match="Platform closed"):
                await manager._session({
                "credential_id": "33333333-3333-4333-8333-333333333333",
                "platform_url": f"http://127.0.0.1:{port}",
                "robot_id": "44444444-4444-4444-8444-444444444444",
                })

        assert [frame["type"] for frame in received] == ["hello", "ui_status", "heartbeat"]
        assert received[0]["sequence"] == 1
        assert received[0]["payload"]["executions"] == [{"execution_id": EXECUTION, "state": "starting"}]
        assert received[1]["sequence"] == 2
        assert received[1]["payload"] == {"execution_id": EXECUTION, "state": "ready"}
        assert received[2]["sequence"] == 3
        assert manager.connected is False

    asyncio.run(scenario())
