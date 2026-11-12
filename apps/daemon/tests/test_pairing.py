import asyncio
import json
import os
import stat

import httpx

from ringfour_daemon.config import Settings
from ringfour_daemon.db import migrate
from ringfour_daemon.enrollment import load_identity
from ringfour_daemon.pairing import pending_path, run_pairing


def test_pending_pairing_survives_restart_and_commits_only_once(tmp_path, capsys):
    async def scenario():
        settings = Settings(db_path=str(tmp_path / "daemon.db"), runner_url="http://runner",
                            platform_url="http://platform", credential_path=str(tmp_path / "secret"))
        migrate(settings.db_path)
        starts = []
        claimed = False
        recorded_secret = None
        robot = "7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e"
        organization = "9b8a7c6d-5e4f-4a3b-8c7d-6e5f4a3b2c1d"
        credential = "3c4d5e6f-7a8b-4c9d-0e1f-2a3b4c5d6e7f"

        def handler(request):
            nonlocal recorded_secret
            if request.url.path == "/v1/device/pairing-requests":
                starts.append(json.loads(request.content))
                return httpx.Response(200, json={"expires_at": "2030-01-01T00:00:00Z"})
            recorded_secret = json.loads(request.content)["credential_secret"]
            return httpx.Response(200, json={"state": "claimed" if claimed else "pending",
                                  "robot_id": robot if claimed else None,
                                  "organization_id": organization if claimed else None,
                                  "credential_id": credential if claimed else None})

        factory = lambda **kwargs: httpx.AsyncClient(transport=httpx.MockTransport(handler), **kwargs)
        first = asyncio.create_task(run_pairing(settings, sleep=lambda _: asyncio.sleep(0.01), client_factory=factory))
        while recorded_secret is None:
            await asyncio.sleep(0.01)
        first.cancel()
        await asyncio.gather(first, return_exceptions=True)
        pending = json.loads(pending_path(settings).read_text())
        assert stat.S_IMODE(os.stat(pending_path(settings)).st_mode) == 0o600
        assert pending["secret"] == recorded_secret
        assert load_identity(settings.db_path) is None
        claimed = True
        second = asyncio.create_task(run_pairing(settings, sleep=lambda _: asyncio.sleep(0.01), client_factory=factory))
        for _ in range(100):
            if load_identity(settings.db_path):
                break
            await asyncio.sleep(0.01)
        second.cancel()
        await asyncio.gather(second, return_exceptions=True)
        assert starts[0] == starts[-1]
        assert load_identity(settings.db_path)["robot_id"] == robot
        assert (tmp_path / "secret").read_text() == recorded_secret
        assert not pending_path(settings).exists()
        assert recorded_secret not in capsys.readouterr().out

    asyncio.run(scenario())


def test_expired_code_is_replaced(tmp_path):
    async def scenario():
        settings = Settings(db_path=str(tmp_path / "daemon.db"), runner_url="http://runner",
                            platform_url="http://platform", credential_path=str(tmp_path / "secret"))
        migrate(settings.db_path)
        starts = []

        def handler(request):
            if request.url.path == "/v1/device/pairing-requests":
                starts.append(json.loads(request.content))
                return httpx.Response(200, json={"expires_at": "2030-01-01T00:00:00Z"})
            if len(starts) == 1:
                return httpx.Response(410)
            return httpx.Response(200, json={"state": "pending"})

        factory = lambda **kwargs: httpx.AsyncClient(transport=httpx.MockTransport(handler), **kwargs)
        task = asyncio.create_task(run_pairing(settings, sleep=lambda _: asyncio.sleep(0.01), client_factory=factory))
        for _ in range(100):
            if len(starts) >= 2:
                break
            await asyncio.sleep(0.01)
        task.cancel()
        await asyncio.gather(task, return_exceptions=True)
        assert len(starts) >= 2
        assert starts[0]["request_id"] != starts[1]["request_id"]
        assert starts[0]["code_hash"] != starts[1]["code_hash"]
        assert starts[0]["credential_hash"] != starts[1]["credential_hash"]

    asyncio.run(scenario())


def test_url_change_replaces_only_unregistered_request(tmp_path):
    async def scenario():
        old = Settings(db_path=str(tmp_path / "daemon.db"), runner_url="http://runner",
                       platform_url="http://localhost:8080", credential_path=str(tmp_path / "secret"))
        migrate(old.db_path)
        from ringfour_daemon.pairing import create_pending

        previous = create_pending(old)
        new = Settings(db_path=old.db_path, runner_url=old.runner_url,
                       platform_url="http://platform", credential_path=old.credential_path)
        starts = []

        def handler(request):
            if request.url.path.endswith("pairing-requests"):
                starts.append(json.loads(request.content))
                return httpx.Response(200, json={"expires_at": "2030-01-01T00:00:00Z"})
            return httpx.Response(200, json={"state": "pending"})

        factory = lambda **kwargs: httpx.AsyncClient(transport=httpx.MockTransport(handler), **kwargs)
        task = asyncio.create_task(run_pairing(new, sleep=lambda _: asyncio.sleep(0.01), client_factory=factory))
        for _ in range(100):
            if starts:
                break
            await asyncio.sleep(0.01)
        task.cancel()
        await asyncio.gather(task, return_exceptions=True)
        saved = json.loads(pending_path(new).read_text())
        assert starts and saved["platform_url"] == "http://platform"
        assert saved["request_id"] != previous["request_id"]

        # A request already confirmed by the old platform must not be lost.
        saved["platform_url"] = old.platform_url
        saved["expires_at"] = "2030-01-01T00:00:00Z"
        pending_path(new).write_text(json.dumps(saved))
        starts.clear()
        task = asyncio.create_task(run_pairing(new, sleep=lambda _: asyncio.sleep(0.01), client_factory=factory))
        await asyncio.sleep(0.05)
        task.cancel()
        await asyncio.gather(task, return_exceptions=True)
        assert not starts
        assert json.loads(pending_path(new).read_text()) == saved

    asyncio.run(scenario())


def test_platform_connection_error_is_concise(tmp_path, caplog):
    async def scenario():
        settings = Settings(db_path=str(tmp_path / "daemon.db"), runner_url="http://runner",
                            platform_url="http://platform", credential_path=str(tmp_path / "secret"))
        migrate(settings.db_path)

        def handler(request):
            raise httpx.ConnectError("refused", request=request)

        factory = lambda **kwargs: httpx.AsyncClient(transport=httpx.MockTransport(handler), **kwargs)
        task = asyncio.create_task(run_pairing(settings, sleep=lambda _: asyncio.sleep(0.01), client_factory=factory))
        for _ in range(100):
            if caplog.records:
                break
            await asyncio.sleep(0.01)
        task.cancel()
        await asyncio.gather(task, return_exceptions=True)
        assert any("check PLATFORM_API_URL" in record.message for record in caplog.records)
        assert all(record.exc_info is None for record in caplog.records)

    asyncio.run(scenario())
