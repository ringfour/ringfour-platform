import asyncio
import json

import httpx

from ringfour_daemon import local_advertisement
from ringfour_daemon.config import Settings


def test_enabled_advertisement_uses_machine_credential_and_pin(tmp_path, monkeypatch):
    credential = tmp_path / "credential"
    credential.write_text("machine-test-secret")
    credential.chmod(0o600)
    settings = Settings(db_path=str(tmp_path / "db"), runner_url="http://runner", credential_path=str(credential),
        local_dev_enabled=True, local_tls_path=str(tmp_path / "tls.pem"), local_advertised_url="https://robot.local:8084")
    monkeypatch.setattr(local_advertisement, "load_identity", lambda _: {"credential_id": "machine-id", "platform_url": "https://platform"})
    calls = []
    real_client = httpx.AsyncClient

    def handler(request):
        calls.append(request)
        return httpx.Response(204)

    monkeypatch.setattr(local_advertisement.httpx, "AsyncClient", lambda **kwargs: real_client(transport=httpx.MockTransport(handler), **kwargs))

    async def stop(_):
        raise asyncio.CancelledError

    async def go():
        try:
            await local_advertisement.publish_local_endpoint(settings, sleep=stop)
        except asyncio.CancelledError:
            pass

    asyncio.run(go())
    assert len(calls) == 1 and calls[0].url.path == "/v1/device/local-dev-endpoint"
    assert calls[0].headers["x-ringfour-credential-id"] == "machine-id"
    assert calls[0].headers["x-ringfour-credential-secret"] == "machine-test-secret"
    body = json.loads(calls[0].content)
    assert len(body["certificate_sha256"]) == 64
    assert body["daemon_url"] == "https://robot.local:8084"
    assert "machine-test-secret" not in calls[0].content.decode()
