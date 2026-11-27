import io
import json
import zipfile
from pathlib import Path
from types import SimpleNamespace

import httpx
import pytest
import typer
from ringfour_cli.development_commands import Client, register, snapshot
from typer.testing import CliRunner


def test_source_snapshot_is_stable_and_excludes_models_and_hidden_files(tmp_path):
    (tmp_path / "main.py").write_text('print("hello")')
    (tmp_path / ".env").write_text("SECRET")
    (tmp_path / "models").mkdir()
    (tmp_path / "models" / "large.pt").write_bytes(b"model")
    (tmp_path / "weights.onnx").write_bytes(b"model")
    first = snapshot(tmp_path)
    assert first == snapshot(tmp_path)
    with zipfile.ZipFile(io.BytesIO(first)) as archive:
        assert archive.namelist() == ["main.py"]
    (tmp_path / "main.py").write_text('print("changed")')
    assert snapshot(tmp_path) != first


def test_source_symlink_and_oversize_file_are_rejected(tmp_path):
    (tmp_path / "main.py").symlink_to("/etc/passwd")
    with pytest.raises(ValueError):
        snapshot(tmp_path)
    (tmp_path / "main.py").unlink()
    (tmp_path / "main.py").write_bytes(b"x" * (8 * 1024 * 1024 + 1))
    with pytest.raises(ValueError, match="8 MiB"):
        snapshot(tmp_path)


def test_context_cannot_cross_platforms_and_never_stores_grants(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    client = Client(SimpleNamespace(api_url="http://platform"), None)
    session = {
        "session_id": "00000000-0000-0000-0000-000000000002",
        "installation_id": "00000000-0000-0000-0000-000000000003",
    }
    context = client.remember("00000000-0000-0000-0000-000000000001", session)
    assert client.context() == context
    assert set(context) == {"api_url", "robot_id", "session_id", "installation_id"}
    client.settings.api_url = "http://other"
    with pytest.raises(ValueError, match="another Platform"):
        client.context()


def test_enable_posts_real_app_binding_and_saves_project_context(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    robot = "00000000-0000-0000-0000-000000000001"
    installation = "00000000-0000-0000-0000-000000000002"
    session = "00000000-0000-0000-0000-000000000003"
    calls = []

    def request(method, path, *args, **kwargs):
        calls.append((method, path, kwargs))
        return httpx.Response(
            200,
            json={"robot_id": robot}
            if method == "GET"
            else {"session_id": session, "installation_id": installation},
        )

    root = typer.Typer()
    register(root, lambda: SimpleNamespace(api_url="http://platform"), request)
    result = CliRunner().invoke(root, ["dev", "enable", installation])
    assert result.exit_code == 0, result.output
    assert calls[1][1] == f"/v1/robots/{robot}/app-development"
    assert calls[1][2]["json"] == {"installation_id": installation, "ttl_seconds": 3600}
    assert (
        json.loads(Path(".ringfour/development.json").read_text())["session_id"]
        == session
    )


@pytest.mark.parametrize("committed_before_disconnect", [False, True])
def test_sync_recovers_lost_upload_reply_without_second_generation(
    tmp_path, monkeypatch, committed_before_disconnect
):
    from ringfour_cli import development_commands as dev

    monkeypatch.chdir(tmp_path)
    (tmp_path / "main.py").write_text('print("source")')
    robot = "00000000-0000-0000-0000-000000000001"
    session = "00000000-0000-0000-0000-000000000002"
    installation = "00000000-0000-0000-0000-000000000003"
    settings = SimpleNamespace(api_url="http://platform")
    Client(settings, None).remember(
        robot, {"session_id": session, "installation_id": installation}
    )
    state = {"generation": 0, "archive_sha256": None}
    puts = []
    monkeypatch.setattr(
        Client, "grant", lambda *args: ("https://robot", "pin", "token")
    )

    def local(_connection, method, _path, *, raw=None, headers=None):
        if method == "GET":
            return state.copy()
        puts.append(headers.copy())
        if len(puts) == 1:
            if committed_before_disconnect:
                state.update(generation=1, archive_sha256=headers["X-Archive-Sha256"])
            raise OSError("Lost connection before reply")
        state.update(generation=1, archive_sha256=headers["X-Archive-Sha256"])
        return state.copy()

    monkeypatch.setattr(dev, "local", local)

    def request(*args, **kwargs):
        return httpx.Response(200, json={"session": {"session_id": session, **state}})

    root = typer.Typer()
    register(root, lambda: settings, request)
    runner = CliRunner()
    assert runner.invoke(root, ["dev", "sync"]).exit_code == 1
    result = runner.invoke(root, ["dev", "sync"])
    assert result.exit_code == 0, result.output
    assert state["generation"] == 1
    assert len(puts) == (1 if committed_before_disconnect else 2)
    if len(puts) == 2:
        assert puts[0] == puts[1]
    assert not (tmp_path / ".ringfour/development-sync.json").exists()


def test_lost_start_reply_reuses_key_and_recovers_the_same_run(tmp_path, monkeypatch):
    from ringfour_cli.development_commands import start_once

    monkeypatch.chdir(tmp_path)
    settings = SimpleNamespace(api_url="http://platform")
    calls = []

    def request(method, path, *args, **kwargs):
        calls.append((path, kwargs))
        if len(calls) == 1:
            raise OSError("Reply lost after accepting start")
        return httpx.Response(202, json={"run_id": "same-run"})

    client = Client(settings, request)
    context = client.remember(
        "robot", {"session_id": "session", "installation_id": "installation"}
    )
    body = {
        "expected_revision_id": "revision",
        "expected_configuration_revision_id": "configuration",
        "development_session_id": "session",
    }
    with pytest.raises(OSError):
        start_once(client, context, body)
    assert start_once(client, context)["run_id"] == "same-run"
    assert calls[0] == calls[1]
    assert (
        json.loads(Path(".ringfour/development.json").read_text())["run_id"]
        == "same-run"
    )
    assert not Path(".ringfour/development-start.json").exists()
