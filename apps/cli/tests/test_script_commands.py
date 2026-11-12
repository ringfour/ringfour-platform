import base64
import hashlib

import httpx
import pytest
from typer.testing import CliRunner

from ringfour_cli import main

cli = CliRunner()


@pytest.fixture()
def fake_api(monkeypatch):
    calls = []
    state = {"execution": "succeeded", "exit_code": 0}
    monkeypatch.setattr(main, "_settings", lambda: object())
    monkeypatch.setattr(main, "_resolve_org", lambda settings, org: "organization")
    monkeypatch.setattr(main.auth, "TokenStore", lambda: object())

    def request(method, path, settings, store, **kwargs):
        calls.append((method, path, kwargs))
        if path == "/v1/script-revisions":
            return httpx.Response(201, json={"id": "7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e", "state": "uploading", "upload_url": "/v1/script-revisions/7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e/content"})
        if path.endswith("/script-runs"):
            return httpx.Response(202, json={"operation_id": "execution", "execution_id": "execution"})
        if path == "/v1/executions/execution":
            return httpx.Response(200, json={"state": state["execution"], "exit_code": state["exit_code"]})
        if path.endswith("/output"):
            params = kwargs["params"]
            data = (b"hello\n" if params["stream"] == "stdout" else b"oops\n")[params["offset"]:]
            return httpx.Response(200, json={"offset": params["offset"], "next_offset": params["offset"] + len(data),
                "data_base64": base64.b64encode(data).decode(), "eof": True})
        return httpx.Response(204)

    monkeypatch.setattr(main, "_api_request", request)
    return calls, state


def test_run_uploads_follows_and_reports_failure(tmp_path, fake_api):
    calls, state = fake_api
    script = tmp_path / "hello.py"
    script.write_text("print('hello')\n")
    result = cli.invoke(main.app, ["run", str(script), "--robot", "robot", "--idempotency-key", "retry-key"])
    assert result.exit_code == 0, result.output
    assert "hello" in result.stdout and "oops" in result.stderr
    assert calls[0][2]["headers"]["Idempotency-Key"] == "retry-key:revision"
    assert any(call[1].endswith("/script-runs") and call[2]["headers"]["Idempotency-Key"] == "retry-key:run" for call in calls)
    state.update(execution="failed", exit_code=7)
    result = cli.invoke(main.app, ["run", str(script), "--robot", "robot"])
    assert result.exit_code == 7


def test_detach_does_not_follow(tmp_path, fake_api):
    calls, _ = fake_api
    script = tmp_path / "hello.py"
    script.write_text("print('hello')\n")
    result = cli.invoke(main.app, ["run", str(script), "--robot", "robot", "--detach"])
    assert result.exit_code == 0
    assert not any(call[1] == "/v1/executions/execution" for call in calls)


def test_run_does_not_send_credentials_to_an_external_upload_url(tmp_path, fake_api, monkeypatch):
    calls, _ = fake_api
    original = main._api_request

    def request(method, path, settings, store, **kwargs):
        if path == "/v1/script-revisions":
            calls.append((method, path, kwargs))
            return httpx.Response(201, json={"id": "7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e", "state": "uploading", "upload_url": "https://attacker.invalid/upload"})
        return original(method, path, settings, store, **kwargs)

    monkeypatch.setattr(main, "_api_request", request)
    script = tmp_path / "hello.py"
    script.write_text("pass\n")
    result = cli.invoke(main.app, ["run", str(script), "--robot", "robot"])
    assert result.exit_code == 1
    assert len(calls) == 1


def test_download_checks_digest_and_does_not_overwrite(tmp_path, monkeypatch):
    monkeypatch.setattr(main, "_settings", lambda: object())
    monkeypatch.setattr(main.auth, "TokenStore", lambda: object())
    data = b"artifact data"

    def request(method, path, settings, store, **kwargs):
        if path.endswith("/artifacts"):
            return httpx.Response(200, json={"artifacts": [{"path": "result.txt", "size": len(data), "sha256": hashlib.sha256(data).hexdigest()}]})
        return httpx.Response(200, content=data)

    monkeypatch.setattr(main, "_api_request", request)
    target = tmp_path / "result.txt"
    result = cli.invoke(main.app, ["artifact", "download", "execution", "result.txt", "-o", str(target)])
    assert result.exit_code == 0 and target.read_bytes() == data
    result = cli.invoke(main.app, ["artifact", "download", "execution", "result.txt", "-o", str(target)])
    assert result.exit_code == 1 and target.read_bytes() == data
