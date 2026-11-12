import asyncio
import json
from datetime import UTC, datetime

import httpx

from ringfour_daemon import operations, runner
from ringfour_daemon.config import Settings


def test_runner_fixture_start_uses_stable_operation_id_and_secret(tmp_path, monkeypatch):
    requests = []
    real_client = httpx.AsyncClient

    def handler(request):
        requests.append(request)
        return httpx.Response(201, json={"execution_id": json.loads(request.content)["execution_id"], "state": "queued"})

    monkeypatch.setattr(runner.httpx, "AsyncClient", lambda **kwargs: real_client(transport=httpx.MockTransport(handler), **kwargs))
    settings = Settings(db_path=str(tmp_path / "db"), runner_url="http://runner", stage_dir=str(tmp_path / "stage"), runner_auth_token="test-secret")
    operation = operations.DeviceOperation(
        operation_id="4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b", robot_id="robot", kind="diagnostic.execution.start",
        payload_version=1, payload={"fixture": "hello"}, state="running",
        deadline_at=datetime.now(UTC).isoformat(), updated_at=datetime.now(UTC).isoformat(), result=None,
    )
    first = asyncio.run(runner.execute(settings, operation))
    second = asyncio.run(runner.execute(settings, operation))
    assert first == second
    assert len(requests) == 2
    assert requests[0].headers["authorization"] == "Bearer test-secret"
    body = json.loads(requests[0].content)
    assert body["execution_id"] == operation.operation_id
    assert body == json.loads(requests[1].content)
    assert (tmp_path / "stage" / "fixtures" / "hello" / "main.py").is_file()


def test_runner_conflict_is_mapped_without_exposing_response():
    response = httpx.Response(409, json={"error": {"code": "execution_conflict", "message": "private detail"}})
    try:
        runner.reject_response(response)
    except runner.RunnerRejected as exc:
        assert exc.code == "execution_conflict"
        assert "private detail" not in exc.message
    else:
        raise AssertionError("Expected RunnerRejected")
