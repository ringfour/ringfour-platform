import hashlib
import io
import zipfile

import httpx
import pytest
from ringfour_cli import sdk_release


def test_sdk_cache_rejects_corruption_and_recovers(tmp_path, monkeypatch):
    archive = io.BytesIO()
    with zipfile.ZipFile(archive, "w") as output:
        output.writestr("ringfour/version.py", 'PROTOCOL_VERSION = 1\nCLIENT_VERSION = "1.0.0"\n')
    wheel = archive.getvalue()
    digest = hashlib.sha256(wheel).hexdigest()
    name = "ringfour_sdk-1.0.0-py3-none-any.whl"
    requests = []

    def respond(request):
        requests.append(request.url.path)
        if request.url.path.endswith("SHA256SUMS"):
            return httpx.Response(200, text=f"{digest}  {name}\n")
        return httpx.Response(200, content=wheel)

    original_client = httpx.Client
    monkeypatch.setattr(
        sdk_release.httpx,
        "Client",
        lambda **_: original_client(transport=httpx.MockTransport(respond)),
    )
    monkeypatch.setenv("XDG_CACHE_HOME", str(tmp_path))
    path = sdk_release.fetch("1.0.0", digest)
    assert path.read_bytes() == wheel
    assert sdk_release.fetch("1.0.0", digest) == path
    assert len(requests) == 2
    path.write_bytes(b"corrupt")
    assert sdk_release.fetch("1.0.0", digest).read_bytes() == wheel
    assert len(requests) == 4
    with pytest.raises(ValueError, match="differs"):
        sdk_release.fetch("1.0.0", "0" * 64)
    assert path.read_bytes() == wheel
