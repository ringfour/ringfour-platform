import base64
import time
import uuid
from pathlib import Path

from fastapi.testclient import TestClient
from test_dev_source import make_snapshot

from ringfour_runner.config import Settings
from ringfour_runner.main import create_app


def wait_state(client, execution_id, wanted, headers):
    for _ in range(400):
        result = client.get(f"/v1/executions/{execution_id}", headers=headers).json()
        if result["state"] in wanted:
            return result
        time.sleep(0.05)
    raise AssertionError("Execution did not reach requested state")


def output(client, execution_id, headers):
    response = client.get(f"/v1/executions/{execution_id}/output", params={"stream": "stdout", "offset": 0, "limit": 16384}, headers=headers)
    return base64.b64decode(response.json()["data_base64"])


def test_restart_reuses_environment_and_reprovisions_changed_lock(tmp_path):
    settings = Settings(str(tmp_path / "data"), str(tmp_path / "stage"), "secret")
    headers = {"authorization": "Bearer secret"}
    session, robot = str(uuid.uuid4()), str(uuid.uuid4())
    create = {"session_id": session, "operation_id": str(uuid.uuid4()), "reason": "create", "expires_at": "2099-01-01T00:00:00Z"}
    with TestClient(create_app(settings)) as client:
        assert client.post("/v1/dev-sessions/lifecycle", json=create, headers=headers).status_code == 200
        first = make_snapshot(settings, session, {"main.py": b"print('one', flush=True)\n", "requirements.lock": b""})
        assert client.post("/v1/dev-sessions/sync", json=first, headers=headers).status_code == 200
        restart = {"session_id": session, "robot_id": robot, "operation_id": str(uuid.uuid4()), "execution_id": str(uuid.uuid4()), "generation": 1}
        assert client.post("/v1/dev-sessions/restart", json=restart).status_code == 401
        response = client.post("/v1/dev-sessions/restart", json=restart, headers=headers)
        assert response.status_code == 200
        final = wait_state(client, restart["execution_id"], {"succeeded", "failed"}, headers)
        assert final["state"] == "succeeded", final
        assert b"one" in output(client, restart["execution_id"], headers)
        envs = Path(settings.data_dir) / "dev-sessions" / session / "envs"
        markers = list(envs.glob("*.ready"))
        assert len(markers) == 1
        stamp = markers[0].stat().st_mtime_ns
        assert client.post("/v1/dev-sessions/restart", json=restart, headers=headers).json()["execution"]["execution_id"] == restart["execution_id"]
        second = make_snapshot(settings, session, {"main.py": b"print('two', flush=True)\n", "requirements.lock": b""}, base=1)
        assert client.post("/v1/dev-sessions/sync", json=second, headers=headers).status_code == 200
        stale = {**restart, "operation_id": str(uuid.uuid4()), "execution_id": str(uuid.uuid4())}
        assert client.post("/v1/dev-sessions/restart", json=stale, headers=headers).status_code == 409
        next_run = {**stale, "generation": 2}
        assert client.post("/v1/dev-sessions/restart", json=next_run, headers=headers).status_code == 200
        final = wait_state(client, next_run["execution_id"], {"succeeded", "failed"}, headers)
        assert final["state"] == "succeeded", final
        assert b"two" in output(client, next_run["execution_id"], headers)
        assert list(envs.glob("*.ready")) == markers and markers[0].stat().st_mtime_ns == stamp
        third = make_snapshot(settings, session, {"main.py": b"print('three', flush=True)\n", "requirements.lock": b"# changed lock\n"}, base=2)
        assert client.post("/v1/dev-sessions/sync", json=third, headers=headers).status_code == 200
        third_run = {**next_run, "operation_id": str(uuid.uuid4()), "execution_id": str(uuid.uuid4()), "generation": 3}
        assert client.post("/v1/dev-sessions/restart", json=third_run, headers=headers).status_code == 200
        final = wait_state(client, third_run["execution_id"], {"succeeded", "failed"}, headers)
        assert final["state"] == "succeeded", final
        assert b"three" in output(client, third_run["execution_id"], headers)
        assert len(list(envs.glob("*.ready"))) == 2
        invalid = make_snapshot(settings, session, {"main.py": b"print('bad')\n", "requirements.lock": b"invalid requirement ???\n"}, base=3)
        assert client.post("/v1/dev-sessions/sync", json=invalid, headers=headers).status_code == 200
        invalid_run = {**third_run, "operation_id": str(uuid.uuid4()), "execution_id": str(uuid.uuid4()), "generation": 4}
        assert client.post("/v1/dev-sessions/restart", json=invalid_run, headers=headers).status_code == 200
        failed = wait_state(client, invalid_run["execution_id"], {"succeeded", "failed"}, headers)
        assert failed["state"] == "failed" and failed["error"]["code"] == "preparation_failed"
        assert len(list(envs.glob("*.ready"))) == 2 and markers[0].stat().st_mtime_ns == stamp
        restored = make_snapshot(settings, session, {"main.py": b"print('restored')\n", "requirements.lock": b""}, base=4)
        assert client.post("/v1/dev-sessions/sync", json=restored, headers=headers).status_code == 200
        restored_run = {**invalid_run, "operation_id": str(uuid.uuid4()), "execution_id": str(uuid.uuid4()), "generation": 5}
        assert client.post("/v1/dev-sessions/restart", json=restored_run, headers=headers).status_code == 200
        assert wait_state(client, restored_run["execution_id"], {"succeeded", "failed"}, headers)["state"] == "succeeded"
        assert markers[0].stat().st_mtime_ns == stamp


def test_restart_stops_own_execution_and_close_cleans_up(tmp_path):
    settings = Settings(str(tmp_path / "data"), str(tmp_path / "stage"), "secret")
    headers = {"authorization": "Bearer secret"}
    session, robot = str(uuid.uuid4()), str(uuid.uuid4())
    create = {"session_id": session, "operation_id": str(uuid.uuid4()), "reason": "create", "expires_at": "2099-01-01T00:00:00Z"}
    with TestClient(create_app(settings)) as client:
        assert client.post("/v1/dev-sessions/lifecycle", json=create, headers=headers).status_code == 200
        first = make_snapshot(settings, session, {"main.py": b"import time\nprint('live',flush=True)\ntime.sleep(120)\n", "requirements.lock": b""})
        assert client.post("/v1/dev-sessions/sync", json=first, headers=headers).status_code == 200
        run = {"session_id": session, "robot_id": robot, "operation_id": str(uuid.uuid4()), "execution_id": str(uuid.uuid4()), "generation": 1}
        assert client.post("/v1/dev-sessions/restart", json=run, headers=headers).status_code == 200
        assert wait_state(client, run["execution_id"], {"running", "failed"}, headers)["state"] == "running"
        incompatible = make_snapshot(settings, session, {"main.py": b"pass\n", "requirements.lock": b""}, base=1)
        incompatible["manifest"]["python_requirement"] = ">=99"
        assert client.post("/v1/dev-sessions/sync", json=incompatible, headers=headers).status_code == 200
        invalid_run = {**run, "operation_id": str(uuid.uuid4()), "execution_id": str(uuid.uuid4()), "generation": 2}
        assert client.post("/v1/dev-sessions/restart", json=invalid_run, headers=headers).status_code == 400
        assert client.get(f"/v1/executions/{run['execution_id']}", headers=headers).json()["state"] == "running"
        repaired = make_snapshot(settings, session, {"main.py": b"import time\ntime.sleep(120)\n", "requirements.lock": b""}, base=2)
        assert client.post("/v1/dev-sessions/sync", json=repaired, headers=headers).status_code == 200
        next_run = {**run, "operation_id": str(uuid.uuid4()), "execution_id": str(uuid.uuid4()), "generation": 3}
        response = client.post("/v1/dev-sessions/restart", json=next_run, headers=headers)
        assert response.status_code == 200, response.text
        assert wait_state(client, run["execution_id"], {"cancelled"}, headers)["state"] == "cancelled"
        assert wait_state(client, next_run["execution_id"], {"running", "failed"}, headers)["state"] == "running"
        close = {**create, "operation_id": str(uuid.uuid4()), "reason": "close"}
        response = client.post("/v1/dev-sessions/lifecycle", json=close, headers=headers)
        assert response.status_code == 200, response.text
        assert wait_state(client, next_run["execution_id"], {"cancelled"}, headers)["state"] == "cancelled"
        assert not (Path(settings.data_dir) / "dev-sessions" / session).exists()


def test_restart_rejects_active_production_without_stopping_it(tmp_path):
    from test_executions import setup

    settings, production = setup(tmp_path, "import time\ntime.sleep(120)\n")
    headers = {"authorization": "Bearer test-secret"}
    session, robot = str(uuid.uuid4()), str(uuid.uuid4())
    create = {"session_id": session, "operation_id": str(uuid.uuid4()), "reason": "create", "expires_at": "2099-01-01T00:00:00Z"}
    with TestClient(create_app(settings)) as client:
        assert client.post("/v1/dev-sessions/lifecycle", json=create, headers=headers).status_code == 200
        source = make_snapshot(settings, session, {"main.py": b"pass\n", "requirements.lock": b""})
        assert client.post("/v1/dev-sessions/sync", json=source, headers=headers).status_code == 200
        assert client.post("/v1/executions", json=production, headers=headers).status_code == 201
        assert wait_state(client, production["execution_id"], {"running", "failed"}, headers)["state"] == "running"
        request = {"session_id": session, "robot_id": robot, "operation_id": str(uuid.uuid4()), "execution_id": str(uuid.uuid4()), "generation": 1}
        response = client.post("/v1/dev-sessions/restart", json=request, headers=headers)
        assert response.status_code == 409 and response.json()["error"]["code"] == "execution_conflict"
        assert client.get(f"/v1/executions/{production['execution_id']}", headers=headers).json()["state"] == "running"
        client.post(f"/v1/executions/{production['execution_id']}/stop", headers=headers)
        wait_state(client, production["execution_id"], {"cancelled"}, headers)


def test_runner_restart_does_not_replay_an_uncertain_execution(tmp_path):
    settings = Settings(str(tmp_path / "data"), str(tmp_path / "stage"), "secret")
    headers = {"authorization": "Bearer secret"}
    session, robot = str(uuid.uuid4()), str(uuid.uuid4())
    create = {"session_id": session, "operation_id": str(uuid.uuid4()), "reason": "create", "expires_at": "2099-01-01T00:00:00Z"}
    request = {"session_id": session, "robot_id": robot, "operation_id": str(uuid.uuid4()), "execution_id": str(uuid.uuid4()), "generation": 1}
    with TestClient(create_app(settings)) as client:
        assert client.post("/v1/dev-sessions/lifecycle", json=create, headers=headers).status_code == 200
        source = make_snapshot(settings, session, {"main.py": b"import time\ntime.sleep(120)\n", "requirements.lock": b""})
        assert client.post("/v1/dev-sessions/sync", json=source, headers=headers).status_code == 200
        assert client.post("/v1/dev-sessions/restart", json=request, headers=headers).status_code == 200
        assert wait_state(client, request["execution_id"], {"running", "failed"}, headers)["state"] == "running"
    with TestClient(create_app(settings)) as client:
        replay = client.post("/v1/dev-sessions/restart", json=request, headers=headers)
        assert replay.status_code == 200
        execution = replay.json()["execution"]
        assert execution["state"] == "failed"
        assert execution["error"]["code"] == "interrupted_unknown_outcome"


def test_expiry_stops_running_code_without_platform(tmp_path):
    from datetime import UTC, datetime, timedelta

    settings = Settings(str(tmp_path / "data"), str(tmp_path / "stage"), "secret")
    headers = {"authorization": "Bearer secret"}
    session, robot = str(uuid.uuid4()), str(uuid.uuid4())
    expiry = (datetime.now(UTC) + timedelta(seconds=3)).isoformat().replace("+00:00", "Z")
    create = {"session_id": session, "operation_id": str(uuid.uuid4()), "reason": "create", "expires_at": expiry}
    with TestClient(create_app(settings)) as client:
        assert client.post("/v1/dev-sessions/lifecycle", json=create, headers=headers).status_code == 200
        source = make_snapshot(settings, session, {"main.py": b"import time\ntime.sleep(120)\n", "requirements.lock": b""})
        assert client.post("/v1/dev-sessions/sync", json=source, headers=headers).status_code == 200
        run = {"session_id": session, "robot_id": robot, "operation_id": str(uuid.uuid4()), "execution_id": str(uuid.uuid4()), "generation": 1}
        assert client.post("/v1/dev-sessions/restart", json=run, headers=headers).status_code == 200
        assert wait_state(client, run["execution_id"], {"running", "failed"}, headers)["state"] == "running"
        assert wait_state(client, run["execution_id"], {"cancelled", "failed"}, headers)["state"] == "cancelled"
        for _ in range(100):
            if not (Path(settings.data_dir) / "dev-sessions" / session).exists(): break
            time.sleep(0.05)
        assert not (Path(settings.data_dir) / "dev-sessions" / session).exists()
