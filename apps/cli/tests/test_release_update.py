import hashlib
import json
import subprocess

import httpx
import pytest

from ringfour_cli import release_update


@pytest.fixture(autouse=True)
def installed_version(monkeypatch):
    monkeypatch.setattr(release_update, "__version__", "1.0.2")


def test_cached_notice_is_offline_and_version_aware(tmp_path, monkeypatch):
    monkeypatch.setenv("XDG_CACHE_HOME", str(tmp_path))
    cache = release_update.cache_path()
    cache.parent.mkdir(parents=True)
    cache.write_text(
        json.dumps({"checked_at": release_update.time.time(), "version": "1.0.3"})
    )
    assert "1.0.3" in release_update.cached_notice()
    cache.write_text(
        json.dumps({"checked_at": release_update.time.time(), "version": "0.9.0"})
    )
    assert release_update.cached_notice() is None


def test_corrupt_download_keeps_installed_cli(tmp_path, monkeypatch):
    wheel = b"different bytes"
    filename = "ringfour_cli-1.0.3-py3-none-any.whl"
    checksum = hashlib.sha256(b"expected bytes").hexdigest()

    def respond(request):
        if request.url.path.endswith("/latest"):
            return httpx.Response(
                302,
                headers={
                    "Location": "/ringfour/ringfour-platform/releases/tag/main-123-1"
                },
            )
        if request.url.path.endswith("SHA256SUMS"):
            return httpx.Response(200, text=f"{checksum}  {filename}\n")
        return httpx.Response(200, content=wheel)

    original_client = httpx.Client
    monkeypatch.setattr(
        release_update.httpx,
        "Client",
        lambda **_: original_client(
            transport=httpx.MockTransport(respond), follow_redirects=True
        ),
    )
    monkeypatch.setattr(
        release_update.httpx,
        "stream",
        lambda *a, **k: original_client(transport=httpx.MockTransport(respond)).stream(
            *a, **k
        ),
    )
    monkeypatch.setenv("XDG_CACHE_HOME", str(tmp_path))
    monkeypatch.setattr(
        release_update.subprocess, "run", lambda *a, **k: pytest.fail("installer ran")
    )
    with pytest.raises(ValueError, match="checksum"):
        release_update.update()


def test_failed_staged_install_keeps_installed_cli(tmp_path, monkeypatch):
    wheel = b"verified wheel"
    filename = "ringfour_cli-1.0.3-py3-none-any.whl"
    checksum = hashlib.sha256(wheel).hexdigest()

    def respond(request):
        if request.url.path.endswith("/latest"):
            return httpx.Response(302, headers={"Location": "/ringfour/ringfour-platform/releases/tag/main-123-1"})
        if request.url.path.endswith("SHA256SUMS"):
            return httpx.Response(200, text=f"{checksum}  {filename}\n")
        return httpx.Response(200, content=wheel)

    original_client = httpx.Client
    monkeypatch.setattr(
        release_update.httpx,
        "Client",
        lambda **_: original_client(transport=httpx.MockTransport(respond), follow_redirects=True),
    )
    monkeypatch.setattr(
        release_update.httpx,
        "stream",
        lambda *a, **k: original_client(transport=httpx.MockTransport(respond)).stream(*a, **k),
    )
    monkeypatch.setenv("XDG_CACHE_HOME", str(tmp_path / "cache"))
    monkeypatch.setenv("XDG_DATA_HOME", str(tmp_path / "data"))
    active = tmp_path / "bin" / "ringfour-cli"
    active.parent.mkdir()
    active.write_text("current CLI")

    def fail_install(command, **kwargs):
        assert command[:3] == ["uv", "tool", "install"]
        assert kwargs["env"]["UV_TOOL_DIR"].startswith(str(tmp_path / "data"))
        raise subprocess.CalledProcessError(1, command)

    monkeypatch.setattr(release_update.subprocess, "run", fail_install)
    with pytest.raises(subprocess.CalledProcessError):
        release_update.update()
    assert active.read_text() == "current CLI"
