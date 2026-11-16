import hashlib
from types import SimpleNamespace

import httpx
from typer.testing import CliRunner

from ringfour_cli import main


def test_streamed_download_verifies_and_does_not_leave_partial_files(tmp_path, monkeypatch):
    data = b"artifact" * 40000
    listing = {"artifacts": [{"path": "result.bin", "size": len(data), "sha256": hashlib.sha256(data).hexdigest()}]}
    settings = SimpleNamespace(api_url="http://platform")
    store = SimpleNamespace(load=lambda _: {"access_token": "token"})
    monkeypatch.setattr(main, "_settings", lambda: settings)
    monkeypatch.setattr(main.auth, "TokenStore", lambda: store)
    monkeypatch.setattr(main, "_api_request", lambda *args, **kwargs: SimpleNamespace(json=lambda: listing))
    original = httpx.Client
    payload = [data[:-1]]
    monkeypatch.setattr(main.httpx, "Client", lambda **kwargs: original(transport=httpx.MockTransport(lambda _: httpx.Response(200, content=payload[0])), **kwargs))
    output = tmp_path / "result.bin"
    runner = CliRunner()
    result = runner.invoke(main.app, ["artifact", "download", "run", "result.bin", "--output", str(output)])
    assert result.exit_code != 0 and not output.exists()
    assert list(tmp_path.iterdir()) == []
    payload[0] = data
    assert runner.invoke(main.app, ["artifact", "download", "run", "result.bin", "--output", str(output)]).exit_code == 0
    assert output.read_bytes() == data
    assert runner.invoke(main.app, ["artifact", "download", "run", "result.bin", "--output", str(output)]).exit_code != 0
    assert output.read_bytes() == data
