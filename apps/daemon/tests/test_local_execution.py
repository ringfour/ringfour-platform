import asyncio
import json
import sqlite3
import uuid
from datetime import UTC, datetime

import httpx
from fastapi.testclient import TestClient
from test_local_source import setup

from ringfour_daemon import local_execution, operations
from ringfour_daemon.local_control import create_local_app


def test_restart_records_before_runner_and_reports_only_status(tmp_path, monkeypatch):
    settings, session, token, _ = setup(tmp_path)
    operation, execution, robot = (str(uuid.uuid4()) for _ in range(3))
    payload = {"operation_id": operation, "session_id": session, "execution_id": execution, "robot_id": robot, "generation": 1}
    item = operations.DeviceOperation(operation_id=operation, robot_id=robot, kind="dev.session.restart", payload_version=1,
        payload=payload, state="running", deadline_at=datetime.now(UTC).isoformat(), updated_at=datetime.now(UTC).isoformat(), result=None)
    requests = []
    real_client = httpx.AsyncClient
    snapshot = {"execution_id": execution, "revision_digest": "a" * 64, "state": "running", "created_at": "2026-11-13T10:00:00Z",
                "updated_at": "2026-11-13T10:00:00Z", "exit_code": None, "error": None, "stop_requested": False}

    def handler(request):
        requests.append(request)
        if request.url.host == "runner" and request.method == "POST":
            with sqlite3.connect(settings.db_path) as conn:
                assert conn.execute("SELECT started FROM local_dev_runs WHERE execution_id=?", (execution,)).fetchone()[0] == 0
            return httpx.Response(200, json={"session_id": session, "generation": 1, "execution": snapshot})
        if request.url.host == "runner" and request.url.path.endswith("/output"):
            return httpx.Response(200, json={"execution_id": execution, "stream": "stdout", "offset": 0, "next_offset": 0,
                                             "data_base64": "", "eof": False, "truncated": False})
        if request.url.host == "runner": return httpx.Response(200, json=snapshot)
        return httpx.Response(204)

    monkeypatch.setattr(local_execution.httpx, "AsyncClient", lambda **kwargs: real_client(transport=httpx.MockTransport(handler), **kwargs))
    assert asyncio.run(local_execution.restart(settings, item))["execution"]["execution_id"] == execution
    with sqlite3.connect(settings.db_path) as conn:
        assert conn.execute("SELECT started FROM local_dev_runs WHERE execution_id=?", (execution,)).fetchone()[0] == 1
    monkeypatch.setattr(local_execution, "machine_headers", lambda *_: {"x-ringfour-credential-id": "test"})
    asyncio.run(local_execution.report(settings, {"platform_url": "http://platform"}, session, execution, True))
    report = json.loads(requests[-1].content)
    assert report == {"state": "running", "exit_code": None, "error": None}
    assert "data_base64" not in requests[-1].content.decode()
    with TestClient(create_local_app(settings)) as client:
        path = f"/v1/local/dev-sessions/{session}/executions/{execution}"
        assert client.get(path).status_code == 401
        assert client.get(path.replace(execution, str(uuid.uuid4())), headers={"authorization": f"Bearer {token}"}).status_code == 404
        assert client.get(path, headers={"authorization": f"Bearer {token}"}).json()["state"] == "running"
        assert client.get(path + "/output", params={"stream": "stdout", "offset": 0, "limit": 5}, headers={"authorization": f"Bearer {token}"}).status_code == 200
