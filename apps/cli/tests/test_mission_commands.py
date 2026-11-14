from types import SimpleNamespace

import httpx
import pytest
from typer.testing import CliRunner

from ringfour_cli import main

SOURCE = "7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e"


@pytest.fixture
def setup_cli(tmp_path, monkeypatch):
    (tmp_path / "mission.toml").write_text('schema_version = 1\nname = "hello"\nentrypoint = "main.py"\npython_requirement = ">=3.13,<3.14"\nlock_file = "lock.txt"\n')
    (tmp_path / "main.py").write_text("print('hello')\n")
    (tmp_path / "lock.txt").write_text("")
    monkeypatch.setattr(main, "_settings", lambda: SimpleNamespace(api_url="http://localhost:8080"))
    monkeypatch.setattr(main, "_resolve_org", lambda settings, org: "org")
    monkeypatch.setattr(main.auth, "TokenStore", lambda: object())
    calls = []
    state = {"published": None, "online": ["robot"]}

    def request(method, path, settings, store, **kwargs):
        calls.append((method, path, kwargs))
        if path == "/v1/organizations/org/robots":
            return httpx.Response(200, json={"items": [{"id": r, "enrolled_at": "now"} for r in state["online"]], "next_cursor": None})
        if path.endswith("/status"):
            return httpx.Response(200, json={"online": True})
        if path == "/v1/organizations/different-org/missions":
            return httpx.Response(200, json={"items": [], "next_cursor": None})
        if path == "/v1/organizations/org/missions":
            return httpx.Response(200, json={"items": [{"id": "mission", "name": "hello", "latest_version": 1 if state["published"] else 0}], "next_cursor": None})
        if path == "/v1/script-revisions":
            return httpx.Response(201, json={"id": SOURCE, "state": "uploading", "upload_url": f"/v1/script-revisions/{SOURCE}/content"})
        if path == "/v1/missions/mission/revisions" and method == "GET":
            return httpx.Response(200, json={"items": [state["published"]] if state["published"] else [], "next_cursor": None})
        if path == "/v1/missions/mission/revisions" and method == "POST":
            doc = kwargs["json"]
            state["published"] = {"id": "revision", "mission_id": "mission", "version": 1, "script_revision_id": SOURCE, "manifest": doc["manifest"]}
            return httpx.Response(201, json=state["published"])
        if path.endswith("/deployments"):
            return httpx.Response(202, json={"deployment_id": "deployment", "operation_id": "operation"})
        if path.startswith("/v1/deployments/"):
            return httpx.Response(202, json={"operation_id": "operation"})
        return httpx.Response(204)

    monkeypatch.setattr(main, "_api_request", request)
    return tmp_path, calls, state


def test_folder_first_push_uploads_and_deploys_without_starting(setup_cli):
    path, calls, _state = setup_cli
    result = CliRunner().invoke(main.app, [str(path), "push", "--idempotency-key", "retry"])
    assert result.exit_code == 0, result.output
    assert any(item[0] == "PUT" and item[1].endswith("/content") for item in calls)
    assert calls[-1][1] == "/v1/robots/robot/deployments"
    assert calls[-1][2]["headers"]["Idempotency-Key"] == "retry:deploy"
    assert not any(item[1].endswith("/start") for item in calls)
    result = CliRunner().invoke(main.app, [str(path), "push", "--idempotency-key", "retry"])
    assert result.exit_code == 0, result.output
    assert [c for c in calls if c[0] == "POST" and c[1] == "/v1/missions/mission/revisions"][-1][2]["json"]["expected_version"] == 0


def test_push_requires_unambiguous_robot_before_upload(setup_cli):
    path, calls, state = setup_cli
    state["online"] = ["one", "two"]
    result = CliRunner().invoke(main.app, [str(path), "push"])
    assert result.exit_code == 1
    assert "Specify --robot" in result.output
    assert not any(c[0] == "POST" for c in calls)


def test_mission_action_forwards_idempotency_key(setup_cli):
    _, calls, _ = setup_cli
    result = CliRunner().invoke(main.app, ["mission", "restart", "deployment", "--idempotency-key", "retry"])
    assert result.exit_code == 0, result.output
    assert calls[-1][1] == "/v1/deployments/deployment/restart"
    assert calls[-1][2]["headers"]["Idempotency-Key"] == "retry"


def test_context_commands_resolve_target_and_latest_execution(setup_cli, monkeypatch):
    path, calls, state = setup_cli
    monkeypatch.chdir(path)
    result = CliRunner().invoke(main.app, [".", "push"])
    assert result.exit_code == 0, result.output
    assert (path / "ringfour.context.json").exists()
    original = main._api_request
    state["execution"] = "first"

    def request(method, url, settings, store, **kwargs):
        if method == "GET" and url == "/v1/deployments/deployment":
            return httpx.Response(200, json={"robot_id": "robot", "selected_revision_id": "revision", "candidate_revision_id": None, "last_execution_id": state["execution"]})
        if method == "GET" and url.startswith("/v1/executions/"):
            return httpx.Response(200, json={"deployment_id": "deployment", "mission_revision_id": "revision"})
        return original(method, url, settings, store, **kwargs)

    monkeypatch.setattr(main, "_api_request", request)
    seen = []
    monkeypatch.setattr(main, "_execution_logs", lambda settings, store, execution_id, follow: seen.append((execution_id, follow)))
    result = CliRunner().invoke(main.app, ["start"])
    assert result.exit_code == 0, result.output
    assert calls[-1][1] == "/v1/deployments/deployment/start"
    assert "robot robot" in result.output
    assert CliRunner().invoke(main.app, ["logs", "--follow"]).exit_code == 0
    state["execution"] = "second"
    assert CliRunner().invoke(main.app, ["logs"]).exit_code == 0
    assert seen == [("first", True), ("second", False)]
    subdir = path / "src"
    subdir.mkdir()
    monkeypatch.chdir(subdir)
    assert CliRunner().invoke(main.app, ["status"]).exit_code == 0


def test_stale_context_blocks_actions_and_ambiguous_robot_requires_selection(setup_cli, monkeypatch):
    path, calls, _state = setup_cli
    monkeypatch.chdir(path)
    assert CliRunner().invoke(main.app, [".", "push"]).exit_code == 0
    from ringfour_cli.mission_context import read_context, save_target
    entry = read_context(path)["targets"][0]
    save_target(path, {**entry, "robot_id": "other", "robot_name": "second", "deployment_id": "other-deployment"})
    result = CliRunner().invoke(main.app, ["start"])
    assert result.exit_code == 1
    assert "Multiple matching robots" in result.output
    before = len(calls)
    monkeypatch.setattr(main, "_api_request", lambda method, url, settings, store, **kwargs: httpx.Response(200, json={"robot_id": "other", "selected_revision_id": "different", "candidate_revision_id": None}))
    result = CliRunner().invoke(main.app, ["start", "--robot", "second"])
    assert result.exit_code == 1
    assert "not selected" in result.output
    assert len(calls) == before


def test_context_is_isolated_by_platform_and_org_and_corruption_blocks_push(setup_cli, monkeypatch):
    path, calls, _ = setup_cli
    monkeypatch.chdir(path)
    assert CliRunner().invoke(main.app, [".", "push"]).exit_code == 0
    monkeypatch.setattr(main, "_resolve_org", lambda settings, org: "different-org")
    result = CliRunner().invoke(main.app, ["start"])
    assert result.exit_code == 1 and "No matching mission context" in result.output
    (path / "ringfour.context.json").write_text("broken")
    before = len(calls)
    result = CliRunner().invoke(main.app, [".", "push"])
    assert result.exit_code == 1 and "Invalid ringfour.context.json" in result.output
    assert len(calls) == before


@pytest.mark.parametrize("prefix", [(".", "push"), ("mission", "push", ".")])
def test_push_start_waits_for_preparation_and_follow_uses_new_execution(setup_cli, monkeypatch, prefix):
    path, calls, _ = setup_cli
    monkeypatch.chdir(path)
    monkeypatch.setattr(main, "_target_robot", lambda _settings, _store, _org, robot: ("robot", robot))
    original = main._api_request
    checks = {"count": 0}

    def request(method, url, settings, store, **kwargs):
        if method == "GET" and url == "/v1/deployments/deployment":
            checks["count"] += 1
            return httpx.Response(200, json={"robot_id": "robot", "candidate_revision_id": None, "selected_revision_id": "revision", "last_operation_id": "operation", "last_error": None, "last_execution_id": "exec"})
        if method == "POST" and url == "/v1/deployments/deployment/start":
            calls.append((method, url, kwargs))
            return httpx.Response(202, json={"operation_id": "start-op", "execution_id": "exec"})
        return original(method, url, settings, store, **kwargs)

    monkeypatch.setattr(main, "_api_request", request)
    seen = []
    monkeypatch.setattr(main, "_execution_logs", lambda settings, store, execution_id, follow: seen.append((execution_id, follow)) or {"state": "succeeded"})
    result = CliRunner().invoke(main.app, [*prefix, "--robot", "base101", "--start", "--follow", "--idempotency-key", "retry"])
    assert result.exit_code == 0, result.output
    assert checks["count"] >= 2
    assert calls[-1][1] == "/v1/deployments/deployment/start"
    assert calls[-1][2]["headers"]["Idempotency-Key"] == "retry:start"
    assert seen == [("exec", True)]


def test_push_start_never_starts_after_failed_preparation(setup_cli, monkeypatch):
    path, calls, _ = setup_cli
    original = main._api_request

    def request(method, url, settings, store, **kwargs):
        if method == "GET" and url == "/v1/deployments/deployment":
            return httpx.Response(200, json={"candidate_revision_id": None, "selected_revision_id": "revision", "last_operation_id": "operation", "last_error": {"message": "bad lock"}})
        return original(method, url, settings, store, **kwargs)

    monkeypatch.setattr(main, "_api_request", request)
    result = CliRunner().invoke(main.app, [str(path), "push", "--start"])
    assert result.exit_code == 1 and "bad lock" in result.output
    assert not any(url.endswith("/start") for _, url, _ in calls)


def test_missing_context_recovers_unambiguous_deployment_from_platform(setup_cli, monkeypatch):
    path, _calls, _state = setup_cli
    monkeypatch.chdir(path)
    original = main._api_request

    def request(method, url, settings, store, **kwargs):
        if url == "/v1/organizations/org/missions":
            return httpx.Response(200, json={"items": [{"id": "mission", "name": "hello"}], "next_cursor": None})
        if url == "/v1/missions/mission/revisions":
            return httpx.Response(200, json={"items": [{"id": "revision"}], "next_cursor": None})
        if url == "/v1/organizations/org/robots":
            return httpx.Response(200, json={"items": [{"id": "robot", "name": "base101"}], "next_cursor": None})
        if url == "/v1/organizations/org/deployments":
            return httpx.Response(200, json={"items": [{"id": "deployment", "robot_id": "robot", "selected_revision_id": "revision", "candidate_revision_id": None}], "next_cursor": None})
        if url == "/v1/deployments/deployment" and method == "GET":
            return httpx.Response(200, json={"robot_id": "robot", "selected_revision_id": "revision", "candidate_revision_id": None})
        return original(method, url, settings, store, **kwargs)

    monkeypatch.setattr(main, "_api_request", request)
    result = CliRunner().invoke(main.app, ["start"])
    assert result.exit_code == 0, result.output
    assert "Recovered ringfour.context.json" in result.output
    assert (path / "ringfour.context.json").exists()


def test_push_rejects_api_urls_containing_secrets_before_any_request(setup_cli, monkeypatch):
    path, calls, _ = setup_cli
    monkeypatch.setattr(main, "_settings", lambda: SimpleNamespace(api_url="https://user:secret@example.test/api"))
    result = CliRunner().invoke(main.app, [str(path), "push"])
    assert result.exit_code == 1
    assert "without credentials" in result.output
    assert "secret" not in result.output
    assert not calls
    assert not (path / "ringfour.context.json").exists()
