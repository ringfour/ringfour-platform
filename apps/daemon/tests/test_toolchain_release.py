import hashlib

import httpx
import pytest
from ringfour_daemon import toolchain_release


def test_verified_release_cache_survives_offline_and_rejects_changed_checksum(
    tmp_path, monkeypatch
):
    wheel = b"Agent release wheel"
    checksum = hashlib.sha256(wheel).hexdigest()
    filename = "ringfour_agent-1.0.0-py3-none-any.whl"
    requests = []

    def respond(request):
        requests.append(request.url.path)
        if request.url.path.endswith("SHA256SUMS"):
            return httpx.Response(200, text=f"{checksum}  {filename}\n")
        return httpx.Response(200, content=wheel)

    client_factory = httpx.Client
    monkeypatch.setattr(
        toolchain_release.httpx,
        "Client",
        lambda **_: client_factory(transport=httpx.MockTransport(respond)),
    )
    cache = tmp_path / "wheels"
    path = toolchain_release.fetch("agent", "1.0.0", checksum, cache)
    assert path.read_bytes() == wheel
    assert toolchain_release.fetch("agent", "1.0.0", checksum, cache) == path
    assert len(requests) == 2
    path.write_bytes(b"corrupt")
    with pytest.raises(ValueError, match="differs"):
        toolchain_release.fetch("agent", "1.0.0", "0" * 64, cache)
    assert path.read_bytes() == b"corrupt"
    assert (
        toolchain_release.fetch("agent", "1.0.0", checksum, cache).read_bytes() == wheel
    )
