"""Live-runner safety checks use a fake transport; they never touch a robot."""

import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace

import httpx
import pytest

ROOT = Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location(
    "apps_live_acceptance", ROOT / "tests/e2e/apps-live-acceptance.py"
)
live = importlib.util.module_from_spec(spec)
spec.loader.exec_module(live)


@pytest.fixture
def setup(monkeypatch, tmp_path):
    state = {
        "busy": False,
        "ambiguous": False,
        "http": [],
        "process": [],
        "connectivity": "online",
        "uninstall": True,
    }
    output = tmp_path / "live.json"
    monkeypatch.setattr(
        live,
        "load_settings",
        lambda: SimpleNamespace(config={"selected_org": "org"}, api_url="http://test"),
    )
    monkeypatch.setattr(
        live,
        "TokenStore",
        lambda: SimpleNamespace(
            load=lambda settings: {"access_token": "test-only-token"}
        ),
    )
    client_type = httpx.Client

    def handler(request):
        state["http"].append((request.method, request.url.path))
        path = request.url.path
        if path.endswith("/robots"):
            robots = [{"id": "robot", "name": "virt2"}]
            if state["ambiguous"]:
                robots.append({"id": "other", "name": "virt2"})
            body = {"items": robots, "next_cursor": None}
        elif path == "/v1/me":
            body = {"user": {"id": "user"}}
        elif path.endswith("/app-status"):
            body = {
                "connectivity": state["connectivity"],
                "supports_app_uninstall": state["uninstall"],
                "last_seen_at": "2026-11-26T13:20:43Z",
            }
        elif path.endswith("/app-runs"):
            body = {
                "items": [{"id": "someone-elses-run"}] if state["busy"] else [],
                "next_cursor": None,
            }
        elif path.endswith("/app-installations"):
            body = {"items": [], "next_cursor": None}
        elif path == "/v1/app-runs/owned-run":
            body = {"state": "running", "installation_id": "owned-install"}
        elif path == "/v1/app-operations/stop-operation":
            body = {"state": "succeeded"}
        elif path == "/v1/app-installations/owned-install":
            body = {"id": "owned-install", "installed": None}
        else:
            raise AssertionError(f"Unexpected request: {path}")
        return httpx.Response(200, json=body)

    monkeypatch.setattr(
        live.httpx,
        "Client",
        lambda **kwargs: client_type(**kwargs, transport=httpx.MockTransport(handler)),
    )

    def process(command, **kwargs):
        state["process"].append(command)
        if str(command[1]).endswith("ap17-live.py"):
            Path(command[command.index("--output") + 1]).write_text(
                json.dumps(
                    {
                        "ap17_live_passed": False,
                        "installation_id": "owned-install",
                        "run_id": "owned-run",
                    }
                )
            )
            return SimpleNamespace(returncode=1)
        assert command[4:8] == ["stop", "owned-install", "--run", "owned-run"]
        return SimpleNamespace(
            returncode=0,
            stdout=json.dumps({"operation_id": "stop-operation"}),
            stderr="",
        )

    monkeypatch.setattr(live.subprocess, "run", process)

    def arguments(*extra):
        monkeypatch.setattr(
            live.sys,
            "argv",
            [
                "live",
                "--robot",
                "virt2",
                "--org",
                "org",
                "--output",
                str(output),
                *extra,
            ],
        )

    return state, output, arguments


def test_preflight_is_read_only_and_does_not_write_credentials(setup):
    state, output, arguments = setup
    arguments("--preflight")
    live.main()
    report = json.loads(output.read_text())
    assert report["preflight_passed"] is True and report["mutations"] is False
    assert not state["process"]
    assert all(method == "GET" for method, path in state["http"])
    assert "test-only-token" not in output.read_text()


@pytest.mark.parametrize("flag", ["busy", "ambiguous"])
def test_preflight_refuses_busy_or_ambiguous_robot_before_mutation(setup, flag):
    state, _output, arguments = setup
    state[flag] = True
    arguments("--preflight")
    with pytest.raises(SystemExit) as error:
        live.main()
    assert error.value.code == 2
    assert not state["process"]
    assert all(method == "GET" for method, path in state["http"])


def test_failed_child_cleanup_targets_only_recorded_own_run(setup):
    state, output, arguments = setup
    arguments("--repository", "registry.invalid/disposable")
    with pytest.raises(AssertionError, match="Live Robot Check/settings stage failed"):
        live.main()
    assert len(state["process"]) == 2
    report = json.loads(output.read_text())
    assert report["passed"] is False
    assert report["cleanup"] == [
        {"run_id": "owned-run", "stopped_or_terminal": True},
        {"installation_id": "owned-install", "removed": True},
    ]


@pytest.mark.parametrize(
    "connectivity,uninstall,expected",
    [
        ("offline", None, "is offline; last seen:"),
        ("online", False, "is online but has not negotiated App uninstall support"),
    ],
)
def test_readiness_refusal_records_specific_reason_without_mutation(
    setup, capsys, connectivity, uninstall, expected
):
    state, output, arguments = setup
    state.update(connectivity=connectivity, uninstall=uninstall)
    arguments("--preflight")
    with pytest.raises(SystemExit) as error:
        live.main()
    assert error.value.code == 2
    report = json.loads(output.read_text())
    assert expected in report["error"]
    assert expected in capsys.readouterr().err
    assert report["mutations"] is False and report["preflight_passed"] is False
    assert report["stage"] == "preflight_failed"
    assert report["readiness"]["connectivity"] == connectivity
    assert not state["process"]
    assert all(method == "GET" for method, path in state["http"])
