import time
from dataclasses import replace
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from threading import Thread

from fastapi.testclient import TestClient
from test_executions import setup

from ringfour_runner.main import create_app
from ringfour_runner.ui import safe_proxy_path


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


def test_ui_proxy_streams_sse_and_strips_runner_credentials(tmp_path):
    observed = {}

    class UiHandler(BaseHTTPRequestHandler):
        def do_GET(self):
            observed["path"] = self.path
            observed["authorization"] = self.headers.get("authorization")
            observed["cookie"] = self.headers.get("cookie")
            payload = b"data: first\n\ndata: second\n\n"
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)

        def log_message(self, *_args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), UiHandler)
    thread = Thread(target=server.serve_forever, daemon=True)
    thread.start()
    settings, body = setup(tmp_path, "import time\ntime.sleep(20)\n")
    settings = replace(settings, ui_port=server.server_address[1])
    headers = {
        "authorization": "Bearer test-secret",
        "cookie": "ringfour_ui_session=platform-secret; gradio=mission-cookie",
    }
    try:
        with TestClient(create_app(settings)) as client:
            assert client.post("/v1/executions", json=body, headers={"authorization": "Bearer test-secret"}).status_code == 201
            for _ in range(100):
                current = client.get(f"/v1/executions/{body['execution_id']}", headers={"authorization": "Bearer test-secret"}).json()
                if current["state"] == "running":
                    break
                time.sleep(0.02)
            response = client.get(
                f"/v1/executions/{body['execution_id']}/ui-proxy/events?session_hash=test",
                headers=headers,
            )
            assert response.status_code == 200
            assert response.headers["content-type"] == "text/event-stream"
            assert response.headers["x-accel-buffering"] == "no"
            assert response.content == b"data: first\n\ndata: second\n\n"
            assert observed["path"] == "/events?session_hash=test"
            assert observed["authorization"] is None
            assert observed["cookie"] == "gradio=mission-cookie"
            too_large = client.post(
                f"/v1/executions/{body['execution_id']}/ui-proxy/upload",
                content=b"x" * (1024 * 1024 + 1), headers={"authorization": "Bearer test-secret"},
            )
            assert too_large.status_code == 413
            denied = client.get(f"/v1/executions/{body['execution_id']}/ui-proxy/events")
            assert denied.status_code == 401
            client.post(f"/v1/executions/{body['execution_id']}/stop", headers={"authorization": "Bearer test-secret"})
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)


def test_ui_proxy_rejects_authority_and_traversal_paths():
    execution_id = "4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b"
    prefix = f"/v1/executions/{execution_id}/ui-proxy".encode()
    assert safe_proxy_path(prefix + b"/gradio_api/queue/data?x=1", execution_id) == "/gradio_api/queue/data?x=1"
    assert safe_proxy_path(prefix + b"//example.org/", execution_id) is None
    assert safe_proxy_path(prefix + b"/%2e%2e/secrets", execution_id) is None
    assert safe_proxy_path(prefix + b"/%252e%252e/secrets", execution_id) is None
