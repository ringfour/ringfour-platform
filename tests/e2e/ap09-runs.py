#!/usr/bin/env python3
"""Actual API/Postgres/Agent/Daemon/Engine runs, replacement and stale-stop safety."""

import importlib.util
import json
import uuid
from pathlib import Path

from ringfour_daemon.db import connect


def acceptance(c):
    client, post, checked, support = [
        c[k] for k in ("client", "post", "checked", "support")
    ]
    project = c["root"] / "run-fixture"
    cli = c["cli"]
    cli(
        "init",
        project,
        "--app-key",
        "run-fixture",
        "--repository",
        c["host"] + "/apps/run-fixture",
    )
    (project / "main.py").write_text(
        'import time\nprint("run.ready",flush=True)\nwhile True: time.sleep(0.1)\n'
    )
    cli("build", project)
    revision = cli("publish", project)
    install = checked(
        post(
            c["path"],
            {
                "revision_id": revision["id"],
                "share_id": None,
                "configuration": {"sample_count": 1},
            },
        )
    )
    assert c["completed"](install)["state"] == "succeeded"
    resource = "/v1/app-installations/" + install["installation_id"]
    binding = checked(client.get(resource))["installed"]
    body = {
        "expected_revision_id": binding["revision_id"],
        "expected_configuration_revision_id": binding["configuration_revision_id"],
    }
    key = str(uuid.uuid4())
    first = checked(post(resource + "/start", body, key))
    assert checked(post(resource + "/start", body, key)) == first
    assert post(resource + "/start", body).status_code == 409

    def run(accepted):
        return checked(client.get("/v1/app-runs/" + accepted["run_id"]))

    support.wait_for(
        lambda: run(first)["state"] == "running", "observed hosted App running"
    )
    assert c["completed"](first)["state"] == "succeeded"
    with connect(c["db"]) as db:
        container = db.execute(
            "SELECT container_id FROM app_runs WHERE run_id=?", (first["run_id"],)
        ).fetchone()[0]
    before = json.loads(
        support.command(["docker", "--host", c["docker_host"], "inspect", container])
    )[0]
    assert before["State"]["Running"]
    support.terminate(c["daemon"])
    replacement = c["start_daemon"]()
    try:
        support.wait_for(
            lambda: c["status"]()["connectivity"] == "online",
            "replacement device connected",
        )
        stop = checked(post(resource + "/stop", {"expected_run_id": first["run_id"]}))
        assert c["completed"](stop)["state"] == "succeeded"
        assert run(first)["state"] == "stopped"
        after = json.loads(
            support.command(
                ["docker", "--host", c["docker_host"], "inspect", container]
            )
        )[0]
        assert before["State"]["StartedAt"] == after["State"]["StartedAt"]
        second = checked(post(resource + "/start", body))
        support.wait_for(lambda: run(second)["state"] == "running", "second run")
        old_stop = checked(
            post(resource + "/stop", {"expected_run_id": first["run_id"]})
        )
        assert c["completed"](old_stop)["state"] == "succeeded"
        assert run(second)["state"] == "running"
        stop = checked(post(resource + "/stop", {"expected_run_id": second["run_id"]}))
        assert c["completed"](stop)["state"] == "succeeded"
        print(
            json.dumps(
                {
                    "ap09_passed": True,
                    "actual_hosted_run": True,
                    "single_slot": True,
                    "daemon_replacement_no_relaunch": True,
                    "old_stop_preserves_new_run": True,
                }
            ),
            flush=True,
        )
    finally:
        support.terminate(replacement)


def main():
    path = Path(__file__).with_name("ap07-install.py")
    spec = importlib.util.spec_from_file_location("install_support", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.main(acceptance)


if __name__ == "__main__":
    main()
