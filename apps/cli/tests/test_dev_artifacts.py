import hashlib
from contextlib import contextmanager
from types import SimpleNamespace

from typer.testing import CliRunner

from ringfour_cli import main

SESSION = "1c775222-008f-43a3-bcef-a61b5f8c80a5"
EXECUTION = "820da32b-8f09-435f-83d5-6c95e88ceefe"
CONNECTION = ("https://127.0.0.1:8084", "a" * 64, "local-token")
CONTENT = b"development artifact\n"
ARTIFACT = {"path": "results/report.txt", "size": len(CONTENT), "sha256": hashlib.sha256(CONTENT).hexdigest()}
MANIFEST = {"execution_id": EXECUTION, "artifacts": [ARTIFACT], "artifact_state": "ready", "artifact_error": None}


class FakeResponse:
    status = 200

    def __init__(self, content):
        self.content = content
        self.offset = 0

    def getheader(self, name):
        return str(len(self.content)) if name.lower() == "content-length" else None

    def read(self, size):
        data = self.content[self.offset:self.offset + size]
        self.offset += len(data)
        return data


def _configure(monkeypatch, tmp_path):
    target = {"session_id": SESSION, "robot_id": "robot-id", "daemon_url": CONNECTION[0]}
    monkeypatch.setattr(main, "_settings", lambda: SimpleNamespace(api_url="https://platform"))
    monkeypatch.setattr(main.auth, "TokenStore", lambda: object())
    monkeypatch.setattr(main, "_dev_project", lambda: tmp_path)
    monkeypatch.setattr(main, "_dev_target", lambda *args: target)
    monkeypatch.setattr(main, "_local_grant", lambda *args: CONNECTION)


def test_dev_artifact_list_uses_local_session_grant(monkeypatch, tmp_path):
    _configure(monkeypatch, tmp_path)
    calls = []
    monkeypatch.setattr(main, "local_request", lambda *args: (calls.append(args) or (200, MANIFEST)))
    result = CliRunner().invoke(main.app, ["dev", "artifact", "list", EXECUTION])
    assert result.exit_code == 0, result.output
    assert calls[0][:4] == (*CONNECTION, "GET")
    assert f"{SESSION}/executions/{EXECUTION}/artifacts" in calls[0][4]
    assert ARTIFACT["path"] in result.output


def test_dev_artifact_download_verifies_bytes_and_does_not_overwrite(monkeypatch, tmp_path):
    _configure(monkeypatch, tmp_path)
    monkeypatch.setattr(main, "_dev_artifact_manifest", lambda *args: (MANIFEST, CONNECTION))
    requests = []

    @contextmanager
    def stream(*args):
        requests.append(args)
        yield FakeResponse(CONTENT)

    monkeypatch.setattr(main, "local_stream", stream)
    output = tmp_path / "report.txt"
    runner = CliRunner()
    result = runner.invoke(main.app, ["dev", "artifact", "download", EXECUTION, ARTIFACT["path"], "--output", str(output)])
    assert result.exit_code == 0, result.output
    assert output.read_bytes() == CONTENT
    assert "%2F" in requests[0][4]
    again = runner.invoke(main.app, ["dev", "artifact", "download", EXECUTION, ARTIFACT["path"], "--output", str(output)])
    assert again.exit_code != 0
    assert output.read_bytes() == CONTENT
    assert list(tmp_path.iterdir()) == [output]


def test_dev_artifact_download_rejects_bad_digest_and_cleans_temporary_file(monkeypatch, tmp_path):
    _configure(monkeypatch, tmp_path)
    monkeypatch.setattr(main, "_dev_artifact_manifest", lambda *args: (MANIFEST, CONNECTION))

    @contextmanager
    def stream(*args):
        yield FakeResponse(b"tampered")

    monkeypatch.setattr(main, "local_stream", stream)
    output = tmp_path / "report.txt"
    result = CliRunner().invoke(main.app, ["dev", "artifact", "download", EXECUTION, ARTIFACT["path"], "--output", str(output)])
    assert result.exit_code != 0
    assert not output.exists()
    assert list(tmp_path.iterdir()) == []
