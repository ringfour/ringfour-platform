import base64
import json

from ringfour_cli import app_project


def test_managed_push_uses_ephemeral_credentials_and_the_reported_manifest_digest(monkeypatch):
    digest = "sha256:" + "a" * 64
    host = "registry.example.com"
    repository = host + "/apps/org-uuid/app-uuid"
    calls = []

    def docker(*args, config=None):
        calls.append(args)
        if args[:2] == ("image", "push"):
            assert args[2:4] == ("--platform", "linux/arm64")
            assert args[4].startswith(repository + ":upload-")
            auth = json.loads((config / "config.json").read_text())
            assert base64.b64decode(auth["auths"][host]["auth"]) == b"ringfour:temporary"
            assert (config / "config.json").stat().st_mode & 0o077 == 0
            return f"upload: digest: {digest} size: 312\n".encode()
        assert config is None
        return b""

    monkeypatch.setattr(app_project, "docker", docker)
    result = app_project.push_managed_image(
        "image-id",
        {"os": "linux", "architecture": "arm64"},
        {"registry": host, "repository": repository, "credential": "temporary"},
    )
    assert result == repository + "@" + digest
    assert calls[0] == ("image", "tag", "image-id", calls[1][-1])
    assert calls[2] == ("image", "rm", calls[1][-1])
