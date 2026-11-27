#!/usr/bin/env python3
"""Opt-in real CLI/Auth0 App acceptance on one explicitly selected idle robot.

Creates a private Robot Check fixture; starts/stops only its own exact run IDs.
Never enrolls devices, grants memberships, commands motion or reloads stacks.
"""

import argparse
import hashlib
import json
import subprocess
import sys
import tempfile
import time
import uuid
from copy import deepcopy
from datetime import UTC, datetime
from pathlib import Path

import httpx
from ringfour_cli.auth import TokenStore
from ringfour_cli.config import load_settings

ROOT = Path(__file__).resolve().parents[2]
ACTIVE = {"accepted", "running"}


def wait(read, label, timeout=180):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = read()
        if value:
            return value
        time.sleep(0.25)
    raise AssertionError(f"Timed out: {label}")


def binding(item):
    installed = item.get("installed") or {}
    return tuple(
        installed.get(key)
        for key in ("revision_id", "configuration_revision_id", "image")
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--robot",
        required=True,
        help="Exact UUID or unambiguous name in the selected CLI workspace",
    )
    parser.add_argument(
        "--org",
        help="Expected selected CLI workspace UUID; prevents accidental use of another workspace",
    )
    parser.add_argument(
        "--repository",
        help="Disposable registry repository for the generated test App",
    )
    parser.add_argument(
        "--preflight",
        action="store_true",
        help="Read identity/robot prerequisites and exit without mutations",
    )
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument(
        "--entrypoint",
        type=Path,
        help="Explicit Robot Check source-preview entrypoint; recorded in evidence",
    )
    args = parser.parse_args()
    if not args.preflight and not args.repository:
        parser.error("Live workflows require --repository")
    settings = load_settings()
    org = settings.config.get("selected_org")
    if not org or (args.org and args.org != org):
        parser.error(
            "Select the intended workspace with the CLI before running this test"
        )
    tokens = TokenStore().load(settings)
    if not tokens or not tokens.get("access_token"):
        parser.error("Log in with ringfour-cli login first")
    report = {
        "passed": False,
        "real_auth0": True,
        "organization_id": org,
        "started_at": datetime.now(UTC).isoformat(),
        "checks": [],
        "cleanup": [],
        "stack_operations": False,
        "two_user_acceptance": "pending",
        "stage": "preflight",
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)

    def save():
        pending = args.output.with_suffix(args.output.suffix + ".tmp")
        pending.write_text(json.dumps(report, indent=2) + "\n")
        pending.replace(args.output)

    def checkpoint(label):
        report["checks"].append(label)
        report["stage"] = label
        save()
        print(f"Live check passed: {label}", flush=True)

    # Use the same workspace CLI interpreter for all calls, not an old global wheel.
    def cli(*arguments):
        command = [
            sys.executable,
            "-c",
            "from ringfour_cli.main import app; app()",
            "app",
            *map(str, arguments),
            "--json",
        ]
        result = subprocess.run(
            command, cwd=ROOT, capture_output=True, text=True, timeout=900, check=False
        )
        if result.returncode:
            raise AssertionError(
                f"CLI {arguments[0]} failed: {result.stdout[-1500:]} {result.stderr[-1500:]}"
            )
        return json.loads(result.stdout)

    with httpx.Client(
        base_url=settings.api_url,
        headers={"Authorization": "Bearer " + tokens["access_token"]},
        timeout=30,
        trust_env=False,
    ) as client:

        def read(path, **params):
            response = client.get(path, params=params or None)
            response.raise_for_status()
            return response.json()

        def page(path, **params):
            items = []
            while True:
                result = read(path, **params)
                items.extend(result["items"])
                if not result.get("next_cursor"):
                    return items
                params["cursor"] = result["next_cursor"]

        def complete(accepted):
            def terminal():
                value = read("/v1/app-operations/" + accepted["operation_id"])
                return (
                    value
                    if value["state"] in ("succeeded", "failed", "timed_out")
                    else None
                )

            result = wait(terminal, "device-confirmed operation", timeout=600)
            assert result["state"] == "succeeded", result
            return result

        def post(path, body, expected=200):
            response = client.post(
                path, json=body, headers={"Idempotency-Key": str(uuid.uuid4())}
            )
            if expected == 200:
                response.raise_for_status()
            else:
                assert response.status_code == expected, (
                    path,
                    response.status_code,
                    response.text[:1000],
                )
            return response.json()

        robots = page(f"/v1/organizations/{org}/robots")
        matches = [
            r for r in robots if r["id"] == args.robot or r["name"] == args.robot
        ]
        if len(matches) != 1:
            parser.error(
                "Robot name/ID must resolve to exactly one robot in the selected workspace"
            )
        robot = matches[0]["id"]
        report["robot_id"] = robot
        report["user_id"] = read("/v1/me")["user"]["id"]
        status = read(f"/v1/robots/{robot}/app-status")
        report["readiness"] = {
            key: status.get(key)
            for key in (
                "connectivity",
                "last_seen_at",
                "support",
                "supports_app_uninstall",
            )
        }

        def refuse_readiness(message):
            report.update(
                stage="preflight_failed",
                error=message,
                mutations=False,
                preflight_passed=False,
                completed_at=datetime.now(UTC).isoformat(),
            )
            save()
            parser.error(message)

        if status["connectivity"] != "online":
            refuse_readiness(
                f"Robot {matches[0]['name']} ({robot}) is {status['connectivity']}; "
                f"last seen: {status.get('last_seen_at') or 'never'}. "
                "Check that its Daemon is running and connected before retrying preflight."
            )
        if not status.get("supports_app_uninstall"):
            refuse_readiness(
                f"Robot {matches[0]['name']} ({robot}) is online but has not negotiated "
                "App uninstall support. An operator must deploy/connect a compatible Daemon."
            )
        if page(
            f"/v1/organizations/{org}/app-runs", active_only="true", robot_id=robot
        ):
            parser.error("Selected robot has an active run; refusing to interfere")
        baseline = {
            i["id"]: binding(i)
            for i in page(f"/v1/robots/{robot}/app-installations")
            if not i.get("removed_at")
        }
        checkpoint("real identity, robot ownership, online/idle and uninstall support")
        if args.preflight:
            report["preflight_passed"] = True
            report["mutations"] = False
            save()
            return
        owned_runs, owned_installations = set(), set()
        imu_report = args.output.with_name("live-imu-settings.json")
        try:
            result = subprocess.run(
                [
                    sys.executable,
                    str(ROOT / "tests/e2e/ap17-live.py"),
                    "--robot",
                    robot,
                    "--repository",
                    args.repository,
                    "--output",
                    str(imu_report),
                    *(
                        ["--entrypoint", str(args.entrypoint)]
                        if args.entrypoint
                        else []
                    ),
                ],
                cwd=ROOT,
                timeout=1200,
                check=False,
            )
            # The progress file records exact owned identities even when the inner test fails.
            progress = json.loads(imu_report.read_text()) if imu_report.exists() else {}
            if progress.get("installation_id"):
                owned_installations.add(progress["installation_id"])
                report["installation_id"] = progress["installation_id"]
            if progress.get("run_id"):
                owned_runs.add(progress["run_id"])
            report["imu_settings_report"] = str(imu_report)
            save()
            assert result.returncode == 0 and progress.get("ap17_live_passed"), (
                "Live Robot Check/settings stage failed; see its progress report"
            )
            report["reference"] = progress
            checkpoint(
                "CLI publish/install, IMU samples, live settings and verified summary artifact"
            )
            installation = progress["installation_id"]
            resource = f"/v1/app-installations/{installation}"
            original = read(resource)["installed"]
            revision = read("/v1/app-revisions/" + original["revision_id"])
            v2_manifest = deepcopy(revision["manifest"])
            v2_manifest["release"] = "2.0.0"
            v2 = post(
                f"/v1/organizations/{org}/app-revisions",
                {"manifest": v2_manifest, "visibility": "unlisted"},
            )
            report["v2_revision_id"] = v2["id"]
            with tempfile.TemporaryDirectory(
                prefix="ringfour-live-values-"
            ) as directory:
                values = Path(directory) / "settings.json"
                values.write_text('{"sample_interval_ms":1000}')
                cli("settings", "set", installation, "--file", values)
                first = cli("start", installation)
                owned_runs.add(first["run_id"])
                wait(
                    lambda: (
                        read("/v1/app-runs/" + first["run_id"])["state"] == "running"
                    ),
                    "own first run started",
                )
                expected = {
                    "expected_revision_id": original["revision_id"],
                    "expected_configuration_revision_id": original[
                        "configuration_revision_id"
                    ],
                }
                post(resource + "/uninstall", expected, expected=409)
                post(
                    resource + "/update",
                    expected
                    | {
                        "revision_id": v2["id"],
                        "share_id": None,
                        "configuration": {"sample_count": 20},
                    },
                    expected=409,
                )
                checkpoint("running update and uninstall are refused")
                complete(cli("stop", installation, "--run", first["run_id"]))
                second = cli("start", installation)
                owned_runs.add(second["run_id"])
                wait(
                    lambda: (
                        read("/v1/app-runs/" + second["run_id"])["state"] == "running"
                    ),
                    "own second run started",
                )
                post(
                    resource + "/stop",
                    {"expected_run_id": first["run_id"]},
                    expected=409,
                )
                assert read("/v1/app-runs/" + second["run_id"])["state"] == "running"
                complete(cli("stop", installation, "--run", second["run_id"]))
                checkpoint("exact stop and stale-stop protection")
                complete(cli("update", installation, v2["id"]))
                assert read(resource)["installed"]["revision_id"] == v2["id"]
                assert not page(
                    f"/v1/organizations/{org}/app-runs",
                    active_only="true",
                    robot_id=robot,
                )
                complete(
                    cli(
                        "rollback",
                        installation,
                        "--revision",
                        original["revision_id"],
                        "--configuration-revision",
                        original["configuration_revision_id"],
                    )
                )
                assert binding(read(resource)) == binding({"installed": original})
                checkpoint(
                    "idle update, no automatic run and exact configuration rollback"
                )
                complete(cli("uninstall", installation, "--yes"))
                assert read(resource)["installed"] is None
                old_run = read("/v1/app-runs/" + progress["run_id"])
                assert old_run["snapshot"]["revision_id"] == original["revision_id"]
                content = client.get(
                    f"/v1/app-runs/{progress['run_id']}/artifacts/{progress['summary_artifact_id']}/content"
                )
                content.raise_for_status()
                assert (
                    hashlib.sha256(content.content).hexdigest()
                    == progress["summary_sha256"]
                )
                checkpoint(
                    "uninstall retains immutable run and verified artifact bytes"
                )
                config = Path(directory) / "startup.json"
                config.write_text('{"sample_count":20}')
                replacement = cli(
                    "install",
                    original["revision_id"],
                    "--robot",
                    robot,
                    "--config",
                    config,
                )
                owned_installations.add(replacement["installation_id"])
                report["replacement_installation_id"] = replacement["installation_id"]
                complete(replacement)
                assert replacement["installation_id"] != installation
                checkpoint("reinstall gets a fresh installation identity")
            after = {
                i["id"]: binding(i)
                for i in page(f"/v1/robots/{robot}/app-installations")
                if not i.get("removed_at")
            }
            assert all(
                after.get(identity) == saved for identity, saved in baseline.items()
            )
            checkpoint("pre-existing installations retain their original bindings")
            report["passed"] = True
        except BaseException as error:
            report["error"] = f"{type(error).__name__}: {error}"
            raise
        finally:
            # Adopt the child's recorded IDs after interruption/failure, never names or unrelated runs.
            if imu_report.exists():
                progress = json.loads(imu_report.read_text())
                if progress.get("installation_id"):
                    owned_installations.add(progress["installation_id"])
                if progress.get("run_id"):
                    owned_runs.add(progress["run_id"])
            for run_id in owned_runs:
                try:
                    run = read("/v1/app-runs/" + run_id)
                    if run["state"] in ACTIVE:
                        complete(cli("stop", run["installation_id"], "--run", run_id))
                    report["cleanup"].append(
                        {"run_id": run_id, "stopped_or_terminal": True}
                    )
                except (
                    httpx.HTTPError,
                    AssertionError,
                    RuntimeError,
                    subprocess.TimeoutExpired,
                    ValueError,
                    KeyError,
                ) as error:
                    report["cleanup"].append({"run_id": run_id, "error": str(error)})
                    report["passed"] = False
            for installation_id in owned_installations:
                try:
                    if read("/v1/app-installations/" + installation_id).get(
                        "installed"
                    ):
                        complete(cli("uninstall", installation_id, "--yes"))
                    report["cleanup"].append(
                        {"installation_id": installation_id, "removed": True}
                    )
                except (
                    httpx.HTTPError,
                    AssertionError,
                    RuntimeError,
                    subprocess.TimeoutExpired,
                    ValueError,
                    KeyError,
                ) as error:
                    report["cleanup"].append(
                        {"installation_id": installation_id, "error": str(error)}
                    )
                    report["passed"] = False
            report["completed_at"] = datetime.now(UTC).isoformat()
            save()
        if not report["passed"]:
            raise RuntimeError(
                "Live cleanup failed; inspect the report before another run"
            )


if __name__ == "__main__":
    main()
