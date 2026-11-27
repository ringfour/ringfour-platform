from types import SimpleNamespace

import httpx
from typer.testing import CliRunner

from ringfour_cli import main

ORG = "11111111-1111-4111-8111-111111111111"
ROBOT = "22222222-2222-4222-8222-222222222222"
APP = "33333333-3333-4333-8333-333333333333"
REVISION = "44444444-4444-4444-8444-444444444444"
INSTALLATION = "55555555-5555-4555-8555-555555555555"
CONFIG_REVISION = "66666666-6666-4666-8666-666666666666"
RUN = "77777777-7777-4777-8777-777777777777"


def setup_commands(monkeypatch):
    monkeypatch.setattr(
        main, "_settings", lambda: SimpleNamespace(api_url="http://api")
    )
    monkeypatch.setattr(main, "_resolve_org", lambda settings, org: org or ORG)
    monkeypatch.setattr(main.auth, "TokenStore", lambda: object())
    monkeypatch.setattr(main, "_idempotency_key", lambda key: key or "generated-key")
    calls = []
    state = {"installations": []}

    def request(method, path, settings, store, **kwargs):
        calls.append((method, path, kwargs))
        if path == f"/v1/organizations/{ORG}/robots":
            return httpx.Response(
                200,
                json={"items": [{"id": ROBOT, "name": "robot-a"}], "next_cursor": None},
            )
        if path == f"/v1/app-revisions/{REVISION}":
            return httpx.Response(
                200,
                json={
                    "id": REVISION,
                    "manifest": {"configuration": {"defaults": {"sample_count": 1}}},
                },
            )
        if path == f"/v1/robots/{ROBOT}/app-installations":
            return httpx.Response(
                200, json={"items": state["installations"], "next_cursor": None}
            )
        if path == f"/v1/app-installations/{INSTALLATION}":
            return httpx.Response(
                200,
                json={
                    "id": INSTALLATION,
                    "app_id": APP,
                    "robot_id": ROBOT,
                    "installed": {
                        "revision_id": REVISION,
                        "configuration_revision_id": CONFIG_REVISION,
                    },
                },
            )
        if method == "POST" and path.endswith("/app-installations"):
            return httpx.Response(
                202,
                json={"installation_id": INSTALLATION, "operation_id": "op-install"},
            )
        if method == "POST" and path.endswith("/start"):
            return httpx.Response(
                202,
                json={
                    "installation_id": INSTALLATION,
                    "operation_id": "op-start",
                    "run_id": RUN,
                },
            )
        if method == "POST" and path.endswith("/stop"):
            return httpx.Response(
                202,
                json={
                    "installation_id": INSTALLATION,
                    "operation_id": "op-stop",
                    "run_id": RUN,
                },
            )
        if method == "POST" and path.endswith("/rollback"):
            return httpx.Response(
                202,
                json={"installation_id": INSTALLATION, "operation_id": "op-rollback"},
            )
        return httpx.Response(
            404, json={"error": {"code": "not_found", "message": "missing"}}
        )

    monkeypatch.setattr(main, "_api_request", request)
    return calls, state


def test_install_uses_selected_workspace_robot_and_release_defaults(monkeypatch):
    calls, _ = setup_commands(monkeypatch)
    result = CliRunner().invoke(
        main.app,
        [
            "app",
            "install",
            REVISION,
            "--robot",
            "robot-a",
            "--idempotency-key",
            "install-key",
        ],
    )
    assert result.exit_code == 0, result.output
    method, path, kwargs = calls[-1]
    assert (method, path) == ("POST", f"/v1/robots/{ROBOT}/app-installations")
    assert kwargs["json"] == {
        "revision_id": REVISION,
        "share_id": None,
        "configuration": {"sample_count": 1},
    }
    assert kwargs["headers"]["Idempotency-Key"] == "install-key"
    assert any(call[1] == f"/v1/organizations/{ORG}/robots" for call in calls)


def test_start_can_resolve_app_on_robot_and_stop_is_bound_to_exact_run(monkeypatch):
    calls, state = setup_commands(monkeypatch)
    state["installations"] = [
        {
            "id": INSTALLATION,
            "app_id": APP,
            "robot_id": ROBOT,
            "installed": {
                "revision_id": REVISION,
                "configuration_revision_id": CONFIG_REVISION,
            },
        }
    ]
    runner = CliRunner()
    started = runner.invoke(
        main.app,
        ["app", "start", APP, "--robot", "robot-a", "--idempotency-key", "start-key"],
    )
    assert started.exit_code == 0, started.output
    start_call = next(
        call for call in calls if call[0] == "POST" and call[1].endswith("/start")
    )
    assert start_call[1] == f"/v1/app-installations/{INSTALLATION}/start"
    assert start_call[2]["json"] == {
        "expected_revision_id": REVISION,
        "expected_configuration_revision_id": CONFIG_REVISION,
    }
    stopped = runner.invoke(
        main.app,
        ["app", "stop", INSTALLATION, "--run", RUN, "--idempotency-key", "stop-key"],
    )
    assert stopped.exit_code == 0, stopped.output
    stop_call = calls[-1]
    assert stop_call[1] == f"/v1/app-installations/{INSTALLATION}/stop"
    assert stop_call[2]["json"] == {"expected_run_id": RUN}
    assert stop_call[2]["headers"]["Idempotency-Key"] == "stop-key"


def test_start_refuses_ambiguous_app_installation(monkeypatch):
    calls, state = setup_commands(monkeypatch)
    state["installations"] = [
        {
            "id": INSTALLATION,
            "app_id": APP,
            "robot_id": ROBOT,
            "installed": {
                "revision_id": REVISION,
                "configuration_revision_id": CONFIG_REVISION,
            },
        },
        {
            "id": "88888888-8888-4888-8888-888888888888",
            "app_id": APP,
            "robot_id": ROBOT,
            "installed": {
                "revision_id": REVISION,
                "configuration_revision_id": CONFIG_REVISION,
            },
        },
    ]
    result = CliRunner().invoke(main.app, ["app", "start", APP, "--robot", "robot-a"])
    assert result.exit_code != 0
    assert "No unique installation" in result.output
    assert not any(call[0] == "POST" and call[1].endswith("/start") for call in calls)


def test_rollback_targets_prior_release_and_configuration_without_starting(monkeypatch):
    calls, _ = setup_commands(monkeypatch)
    result = CliRunner().invoke(
        main.app,
        [
            "app",
            "rollback",
            INSTALLATION,
            "--revision",
            REVISION,
            "--configuration-revision",
            CONFIG_REVISION,
            "--idempotency-key",
            "rollback-key",
        ],
    )
    assert result.exit_code == 0, result.output
    call = calls[-1]
    assert call[1] == f"/v1/app-installations/{INSTALLATION}/rollback"
    assert call[2]["json"] == {
        "expected_revision_id": REVISION,
        "expected_configuration_revision_id": CONFIG_REVISION,
        "revision_id": REVISION,
        "configuration_revision_id": CONFIG_REVISION,
    }
    assert call[2]["headers"]["Idempotency-Key"] == "rollback-key"
