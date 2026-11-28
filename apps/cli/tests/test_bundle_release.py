import hashlib
import io
import json
import tarfile

import httpx
import pytest

from ringfour_cli import bundle_release


def archive_for(version, *, unsafe=False):
    files = {
        "release.json": json.dumps({"bundle_version": version}).encode(),
        "linux-amd64/wheels/example.whl": b"wheel bytes",
    }
    sums = "".join(
        f"{hashlib.sha256(data).hexdigest()}  {name}\n"
        for name, data in sorted(files.items())
    ).encode()
    files["SHA256SUMS"] = sums
    archive_bytes = io.BytesIO()
    with tarfile.open(fileobj=archive_bytes, mode="w:gz") as archive:
        for name, data in files.items():
            member = tarfile.TarInfo(f"{version}/{name}")
            member.size = len(data)
            archive.addfile(member, io.BytesIO(data))
        if unsafe:
            member = tarfile.TarInfo(f"{version}/../escape")
            member.size = 1
            archive.addfile(member, io.BytesIO(b"x"))
    return archive_bytes.getvalue(), hashlib.sha256(sums).hexdigest()


def test_fetches_verifies_and_reuses_pinned_bundle(tmp_path, monkeypatch):
    version = "1.2.3"
    archive, pin = archive_for(version)
    project = tmp_path / "project"
    project.mkdir()
    (project / "app.json").write_text(json.dumps({"wheel_bundle": {"version": version, "sha256": pin}}))
    monkeypatch.setattr(bundle_release, "validate_authoring", lambda document: document)
    monkeypatch.setenv("XDG_DATA_HOME", str(tmp_path / "data"))
    calls = []

    def respond(request):
        calls.append(request.url.path)
        if request.url.path.endswith("/latest"):
            return httpx.Response(302, headers={"Location": "/ringfour/ringfour-platform/releases/tag/main-2-1"})
        if request.url.path.endswith("SHA256SUMS"):
            return httpx.Response(200, text=f"{hashlib.sha256(archive).hexdigest()}  python-artifacts.tar.gz\n")
        return httpx.Response(200, content=archive)

    original_client = httpx.Client
    monkeypatch.setattr(bundle_release.httpx, "Client", lambda **_: original_client(transport=httpx.MockTransport(respond), follow_redirects=True))
    destination = bundle_release.fetch_project(project)
    assert destination.name == version
    assert (destination / "linux-amd64/wheels/example.whl").read_bytes() == b"wheel bytes"
    before = len(calls)
    assert bundle_release.fetch_project(project) == destination
    assert len(calls) == before


@pytest.mark.parametrize("unsafe", [False, True])
def test_rejects_corrupt_or_unsafe_bundle_without_installing(tmp_path, monkeypatch, unsafe):
    archive, pin = archive_for("1.2.3", unsafe=unsafe)
    project = tmp_path / "project"
    project.mkdir()
    (project / "app.json").write_text(json.dumps({"wheel_bundle": {"version": "1.2.3", "sha256": pin}}))
    monkeypatch.setattr(bundle_release, "validate_authoring", lambda document: document)
    monkeypatch.setenv("XDG_DATA_HOME", str(tmp_path / "data"))

    def respond(request):
        if request.url.path.endswith("/latest"):
            return httpx.Response(302, headers={"Location": "/ringfour/ringfour-platform/releases/tag/main-2-1"})
        if request.url.path.endswith("SHA256SUMS"):
            digest = hashlib.sha256(archive if unsafe else b"different").hexdigest()
            return httpx.Response(200, text=f"{digest}  python-artifacts.tar.gz\n")
        return httpx.Response(200, content=archive)

    original_client = httpx.Client
    monkeypatch.setattr(bundle_release.httpx, "Client", lambda **_: original_client(transport=httpx.MockTransport(respond), follow_redirects=True))
    with pytest.raises(ValueError, match="checksum|unsafe"):
        bundle_release.fetch_project(project)
    assert not (tmp_path / "data/ringfour/python-artifacts/1.2.3").exists()
