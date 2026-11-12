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
    monkeypatch.setattr(main, "_settings", lambda: object())
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
        if path == "/v1/organizations/org/missions":
            return httpx.Response(200, json={"items": [{"id": "mission", "name": "hello", "latest_version": 1 if state["published"] else 0}], "next_cursor": None})
        if path == "/v1/script-revisions":
            return httpx.Response(201, json={"id": SOURCE, "state": "uploading", "upload_url": f"/v1/script-revisions/{SOURCE}/content"})
        if path == "/v1/missions/mission/revisions" and method == "GET":
            return httpx.Response(200, json={"items": [state["published"]] if state["published"] else [], "next_cursor": None})
        if path == "/v1/missions/mission/revisions" and method == "POST":
            doc = kwargs["json"]
            state["published"] = {"id": "revision", "version": 1, "script_revision_id": SOURCE, "manifest": doc["manifest"]}
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
