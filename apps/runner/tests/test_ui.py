import time

from fastapi.testclient import TestClient

from test_executions import setup
from ringfour_runner.main import create_app


def test_active_ui_status_list_requires_runner_auth_and_is_bounded(tmp_path):
    settings, body = setup(tmp_path, "import time\ntime.sleep(3)\n")
    headers = {"authorization": "Bearer test-secret"}
    with TestClient(create_app(settings)) as client:
        assert client.get("/v1/ui").status_code == 401
        assert client.post("/v1/executions", json=body, headers=headers).status_code == 201
        for _ in range(100):
            run = client.get(f"/v1/executions/{body['execution_id']}", headers=headers).json()
            if run["state"] == "running":
                break
            time.sleep(0.02)
        result = client.get("/v1/ui", headers=headers)
        assert result.status_code == 200
        assert result.json()["executions"][0]["execution_id"] == body["execution_id"]
        assert result.json()["executions"][0]["state"] in {"starting", "ready", "unavailable"}
        client.post(f"/v1/executions/{body['execution_id']}/stop", headers=headers)
