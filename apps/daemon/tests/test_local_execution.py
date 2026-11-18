import asyncio
import hashlib
import json
import sqlite3
import uuid
from datetime import UTC, datetime

import httpx
import pytest
from fastapi.testclient import TestClient
from test_local_source import setup

from ringfour_daemon import local_control, local_execution, operations, runner
from ringfour_daemon.local_control import create_local_app


class ArtifactStream(httpx.AsyncByteStream):
    def __init__(self, content):
        self.content = content

    async def __aiter__(self):
        for offset in range(0, len(self.content), 5):
            yield self.content[offset:offset + 5]


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


def test_restart_reports_low_storage_but_preserves_ambiguous_server_failures(tmp_path, monkeypatch):
    settings, session, _, _ = setup(tmp_path)
    operation, execution, robot = (str(uuid.uuid4()) for _ in range(3))
    payload = {"operation_id": operation, "session_id": session, "execution_id": execution, "robot_id": robot, "generation": 21}
    item = operations.DeviceOperation(operation_id=operation, robot_id=robot, kind="dev.session.restart", payload_version=1,
        payload=payload, state="running", deadline_at=datetime.now(UTC).isoformat(), updated_at=datetime.now(UTC).isoformat(), result=None)
    real_client = httpx.AsyncClient
    responses = iter([httpx.Response(503, json={"error": {"code": "insufficient_storage"}}), httpx.Response(503)])
    monkeypatch.setattr(local_execution.httpx, "AsyncClient", lambda **kwargs: real_client(transport=httpx.MockTransport(lambda _: next(responses)), **kwargs))
    with pytest.raises(runner.RunnerRejected) as rejected:
        asyncio.run(local_execution.restart(settings, item))
    assert rejected.value.code == "insufficient_storage"
    with pytest.raises(httpx.HTTPStatusError):
        asyncio.run(local_execution.restart(settings, item))


def test_local_artifact_list_and_download_are_session_scoped_and_streamed(tmp_path, monkeypatch):
    settings, session, token, _ = setup(tmp_path)
    execution, operation = str(uuid.uuid4()), str(uuid.uuid4())
    local_execution.record(settings.db_path, {"session_id": session, "execution_id": execution, "operation_id": operation})
    content = b"development artifact bytes\n"
    digest = hashlib.sha256(content).hexdigest()
    artifact_list = {"execution_id": execution, "artifacts": [{"path": "results/report.txt", "size": len(content), "sha256": digest}],
                     "artifact_state": "ready", "artifact_error": None}
    requests = []
    real_client = httpx.AsyncClient

    def handler(request):
        requests.append(request)
        if request.url.path.endswith("/download"):
            assert request.url.params["path"] == "results/report.txt"
            return httpx.Response(200, stream=ArtifactStream(content), headers={"content-length": str(len(content))})
        return httpx.Response(200, json=artifact_list)

    monkeypatch.setattr(local_control.httpx, "AsyncClient", lambda **kwargs: real_client(transport=httpx.MockTransport(handler), **kwargs))
    headers = {"authorization": f"Bearer {token}"}
    base = f"/v1/local/dev-sessions/{session}/executions/{execution}/artifacts"
    with TestClient(create_local_app(settings)) as client:
        assert client.get(base).status_code == 401
        assert client.get(base.replace(execution, str(uuid.uuid4())), headers=headers).status_code == 404
        listed = client.get(base, headers=headers)
        assert listed.status_code == 200 and listed.json() == artifact_list
        downloaded = client.get(base + "/download", params={"path": "results/report.txt"}, headers=headers)
        assert downloaded.status_code == 200 and downloaded.content == content
        assert downloaded.headers["content-length"] == str(len(content))
    assert len(requests) == 2
    assert all(request.headers["authorization"] == "Bearer secret" for request in requests)
