#!/usr/bin/env python3
"""Opt-in finite live IMU/settings acceptance using the caller's real CLI login.

Publishes/installs one reference App on an explicitly chosen idle robot. Never
restarts stacks, changes device configuration, issues motion, or stops other runs.
"""

import argparse
import base64
import hashlib
import json
import subprocess
import sys
import tempfile
import time
import uuid
from datetime import UTC, datetime
from pathlib import Path

import httpx
from ringfour_cli.auth import TokenStore
from ringfour_cli.config import load_settings


def wait(read, label, timeout=90):
    deadline = time.monotonic() + timeout
    last = None
    while time.monotonic() < deadline:
        last = read()
        if last:
            return last
        time.sleep(0.1)
    raise AssertionError(f"{label}: {last}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--robot", required=True)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument(
        "--entrypoint",
        type=Path,
        help="Explicit source-preview main.py; does not modify the pinned template",
    )
    args = parser.parse_args()
    settings = load_settings()
    token = TokenStore().load(settings)
    if not token or not token.get("access_token"):
        raise RuntimeError("Log in with the CLI first; live tests never bypass Auth0.")
    with httpx.Client(
        base_url=settings.api_url,
        headers={"Authorization": "Bearer " + token["access_token"]},
        timeout=20,
    ) as client:
        org = settings.config["selected_org"]
        robot = client.get("/v1/robots/" + args.robot)
        robot.raise_for_status()
        if robot.json()["organization_id"] != org:
            raise RuntimeError(
                "The robot does not belong to the selected CLI workspace."
            )
        progress = {
            "ap17_live_passed": False,
            "organization_id": org,
            "robot_id": args.robot,
        }

        def checkpoint(**values):
            progress.update(values)
            args.output.parent.mkdir(parents=True, exist_ok=True)
            pending = args.output.with_suffix(args.output.suffix + ".tmp")
            pending.write_text(json.dumps(progress, indent=2) + "\n")
            pending.replace(args.output)

        checkpoint(stage="preflight")
        response = client.get(
            "/v1/organizations/" + org + "/app-runs",
            params={"active_only": "true", "robot_id": args.robot},
        )
        response.raise_for_status()
        if response.json()["items"]:
            raise RuntimeError(
                "The selected robot has an active run; refusing to interfere."
            )
        response = client.get(f"/v1/robots/{args.robot}/app-status")
        response.raise_for_status()
        if response.json()["connectivity"] != "online":
            raise RuntimeError("The selected robot must be online.")

        def checked(response):
            if not response.is_success:
                raise AssertionError(
                    f"{response.status_code} {response.url.path}: {response.text}"
                )
            return response.json()

        def cli(*arguments):
            result = subprocess.run(
                [
                    sys.executable,
                    "-c",
                    "from ringfour_cli.main import app; app()",
                    "app",
                    *map(str, arguments),
                    "--json",
                ],
                capture_output=True,
                text=True,
                timeout=600,
                check=False,
            )
            assert result.returncode == 0, result.stdout + result.stderr
            return json.loads(result.stdout)

        with tempfile.TemporaryDirectory(prefix="ap17-live-") as directory:
            project = Path(directory) / "robot-check"
            cli(
                "init",
                project,
                "--app-key",
                "ap17-robot-check-" + uuid.uuid4().hex[:8],
                "--repository",
                args.repository,
            )
            if args.entrypoint:
                source = args.entrypoint.read_bytes()
                (project / "main.py").write_bytes(source)
                checkpoint(
                    preview_entrypoint=str(args.entrypoint.resolve()),
                    preview_entrypoint_sha256=hashlib.sha256(source).hexdigest(),
                )
            cli("validate", project)
            cli("build", project)
            revision = cli("publish", project)
            checkpoint(
                stage="published", app_id=revision["app_id"], revision_id=revision["id"]
            )
            config = Path(directory) / "startup.json"
            config.write_text('{"sample_count": 20}')
            install = cli(
                "install", revision["id"], "--robot", args.robot, "--config", config
            )
            checkpoint(
                stage="installing",
                installation_id=install["installation_id"],
                operation_id=install["operation_id"],
            )
            operation = wait(
                lambda: (
                    value
                    if (
                        value := checked(
                            client.get("/v1/app-operations/" + install["operation_id"])
                        )
                    )["state"]
                    in ("succeeded", "failed")
                    else None
                ),
                "installed binding",
            )
            assert operation["state"] == "succeeded", operation
            installation = install["installation_id"]
            settings_path = "/v1/app-installations/" + installation + "/settings"
            values = Path(directory) / "live.json"
            values.write_text('{"sample_interval_ms": 1000}')
            assert (
                cli("settings", "set", installation, "--file", values)["state"]
                == "saved_for_next_start"
            )
            accepted = cli("start", installation)
            checkpoint(stage="running", run_id=accepted["run_id"])
            run_path = "/v1/app-runs/" + accepted["run_id"]
            try:
                state = wait(
                    lambda: (
                        value
                        if (value := checked(client.get(settings_path))).get(
                            "last_applied"
                        )
                        else None
                    ),
                    "initial settings confirmed",
                )
                initial = state["last_applied"]["id"]
                values.write_text('{"sample_interval_ms": 250}')
                pending = cli("settings", "set", installation, "--file", values)
                assert pending["state"] == "pending"
                state = wait(
                    lambda: (
                        value
                        if (value := checked(client.get(settings_path))).get(
                            "last_applied", {}
                        )
                        and value["last_applied"]["id"] != initial
                        and value["last_applied"]["values"]["sample_interval_ms"] == 250
                        else None
                    ),
                    "live interval confirmed",
                )
                run = wait(
                    lambda: (
                        value
                        if (value := checked(client.get(run_path)))["state"]
                        not in ("accepted", "running")
                        else None
                    ),
                    "finite IMU run completed",
                )
                assert run["state"] == "succeeded" and run["exit_code"] == 0, run
                output = wait(
                    lambda: (
                        value
                        if (
                            value := checked(
                                client.get(
                                    run_path + "/output",
                                    params={"stream": "stdout", "offset": 0},
                                )
                            )
                        )["sealed"]
                        else None
                    ),
                    "output sealed",
                )
                events = [
                    json.loads(line)
                    for line in base64.b64decode(output["data_base64"])
                    .decode()
                    .splitlines()
                    if line.startswith("{")
                ]
                imu = [
                    event for event in events if event.get("event") == "robot_check.imu"
                ]
                assert len(imu) == 20
                assert any(
                    event.get("event") == "robot_check.settings"
                    and event.get("outcome") == "applied"
                    for event in events
                )
                artifacts = wait(
                    lambda: (
                        value["items"]
                        if (value := checked(client.get(run_path + "/artifacts")))[
                            "items"
                        ]
                        else None
                    ),
                    "summary artifact published",
                )
                summaries = [
                    item
                    for item in artifacts
                    if item["path"] in ("summary.json", "reports/summary.json")
                ]
                assert len(summaries) == 1, [item["path"] for item in artifacts]
                summary = summaries[0]
                content = client.get(
                    run_path + "/artifacts/" + summary["artifact_id"] + "/content"
                )
                content.raise_for_status()
                assert hashlib.sha256(content.content).hexdigest() == summary["sha256"]
                report = {
                    "ap17_live_passed": True,
                    "captured_at": datetime.now(UTC).isoformat(),
                    "real_auth0": True,
                    "user_id": checked(client.get("/v1/me"))["user"]["id"],
                    "organization_id": org,
                    "robot_id": args.robot,
                    "app_id": revision["app_id"],
                    "revision_id": revision["id"],
                    "image": revision["manifest"]["targets"][0]["image"],
                    "installation_id": installation,
                    "run_id": accepted["run_id"],
                    "settings_application_id": pending["application_id"],
                    "imu_samples": len(imu),
                    "summary_artifact_id": summary["artifact_id"],
                    "summary_sha256": summary["sha256"],
                    "summary": content.json(),
                    "run_requested_at": run["requested_at"],
                    "run_started_at": run["started_at"],
                    "run_completed_at": run["completed_at"],
                }
                args.output.parent.mkdir(parents=True, exist_ok=True)
                report.update(
                    {
                        key: value
                        for key, value in progress.items()
                        if key.startswith("preview_")
                    }
                )
                report["summary_path"] = summary["path"]
                checkpoint(stage="completed", **report)
                print(json.dumps(report), flush=True)
            finally:
                current = checked(client.get(run_path))
                if current["state"] in ("accepted", "running"):
                    cli("stop", installation, "--run", accepted["run_id"])


if __name__ == "__main__":
    main()
