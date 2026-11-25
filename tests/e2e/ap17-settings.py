#!/usr/bin/env python3
"""Real packaged settings client, CLI, API, PG, daemon/engine recovery and Files.

Disposable fixtures only. JWT signing is Development-only test authentication;
this does not claim human Auth0 or browser acceptance.
"""

import argparse
import base64
import importlib.util
import json
import os
import subprocess
import threading
import time
import uuid
from pathlib import Path

import httpx
from ringfour_daemon.db import connect

ROOT = Path(__file__).resolve().parents[2]


def load(name):
    spec = importlib.util.spec_from_file_location(
        name.replace("-", "_"), Path(__file__).with_name(name + ".py")
    )
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def wait(read, message, timeout=45):
    deadline = time.monotonic() + timeout
    last = None
    while time.monotonic() < deadline:
        try:
            last = read()
            if last:
                return last
        except (httpx.HTTPError, KeyError, TypeError):
            pass
        time.sleep(0.05)
    raise AssertionError(f"{message}: {last}")


def signed_token(pem):
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import padding

    encode = lambda raw: base64.urlsafe_b64encode(raw).rstrip(b"=")
    key = serialization.load_pem_private_key(pem.encode(), password=None)
    header = encode(
        json.dumps({"alg": "RS256", "typ": "JWT", "kid": "ringfour-test-key"}).encode()
    )
    payload = encode(
        json.dumps(
            {
                "iss": "https://ap03.test/",
                "aud": "https://api.ringfour.eu",
                "sub": "auth0|ap17-" + uuid.uuid4().hex,
                "exp": int(time.time()) + 3600,
            }
        ).encode()
    )
    data = header + b"." + payload
    return (
        data + b"." + encode(key.sign(data, padding.PKCS1v15(), hashes.SHA256()))
    ).decode()


def configure(c, faults):
    def hook(value, phase, status):
        result = (
            value["method"] == "POST"
            and "/v1/runtime/settings/" in value["path"]
            and value["path"].endswith("/result")
        )
        if not result:
            return False
        if phase == "before":
            if faults.get("crash_api"):
                faults["crash_api"] = False
                faults["api"].kill()
                faults["api"].wait(timeout=10)
            return faults.get("pause_results", False)
        if phase == "after" and status == 200 and faults.get("drop_result"):
            faults["drop_result"] = False
            faults["pause_results"] = True
            faults["dropped"].set()
            return True
        return False

    c["runtime_fixture_hook"] = hook
    return load("ap10-runtime").configure(c)


def acceptance(
    c, faults, report_path=None, samples=20, benchmark_only=False, evidence_path=None
):
    client, post, cli, checked = (c[k] for k in ("client", "post", "cli", "checked"))
    project = c["root"] / "settings-fixture"
    cli(
        "init",
        project,
        "--app-key",
        "ap17-settings",
        "--repository",
        c["host"] + "/apps/ap17-settings",
    )
    (project / "main.py").write_text(
        (ROOT / "tests/fixtures/apps/settings/main.py").read_text()
    )
    authoring = json.loads((project / "app.json").read_text())
    authoring["configuration"]["environment"] = {"SAMPLE_COUNT": "sample_count"}
    authoring["capabilities"] = {"required": [], "optional": []}
    (project / "app.json").write_text(json.dumps(authoring))
    cli("validate", project)
    cli("build", project)
    revision = cli("publish", project)
    installed = checked(
        post(
            c["path"],
            {
                "revision_id": revision["id"],
                "share_id": None,
                "configuration": {"sample_count": 2},
            },
        )
    )
    assert c["completed"](installed)["state"] == "succeeded"
    resource = "/v1/app-installations/" + installed["installation_id"]
    binding = checked(client.get(resource))["installed"]
    start_body = {
        key: binding[key.removeprefix("expected_")]
        for key in ("expected_revision_id", "expected_configuration_revision_id")
    }
    settings_path = resource + "/settings"
    processes = []
    daemon = c["daemon"]
    api = c["api"]
    started = []

    def settings():
        return checked(client.get(settings_path))

    def write(value, key=None, state=None):
        state = state or settings()
        body = {
            "values": {"sample_interval_ms": value},
            "expected_selected_revision_id": (
                state.get("selected_for_next_start") or {}
            ).get("id"),
            "expected_applied_revision_id": (state.get("last_applied") or {}).get("id"),
            "expected_run_id": state["active_run_id"],
        }
        return post(settings_path, body, key)

    def run(accepted):
        return checked(client.get("/v1/app-runs/" + accepted["run_id"]))

    def applied(value):
        state = settings()
        return (
            state
            if not state["pending_application"]
            and (state.get("last_applied") or {})
            .get("values", {})
            .get("sample_interval_ms")
            == value
            else False
        )

    def start():
        accepted = checked(post(resource + "/start", start_body))
        started.append(accepted)
        wait(lambda: run(accepted)["state"] == "running", "observed App start")
        return accepted

    def stop(accepted):
        stopped = checked(
            post(resource + "/stop", {"expected_run_id": accepted["run_id"]})
        )
        assert c["completed"](stopped)["state"] == "succeeded"
        assert run(accepted)["state"] == "stopped"

    def output(accepted):
        raw = checked(
            client.get(
                "/v1/app-runs/" + accepted["run_id"] + "/output",
                params={"stream": "stdout", "offset": 0},
            )
        )
        return [
            json.loads(line)
            for line in base64.b64decode(raw["data_base64"]).decode().splitlines()
            if line.startswith("{")
        ]

    try:
        if benchmark_only:
            load("ap19-backend").benchmark(
                c
                | {
                    "bench_resource": resource,
                    "bench_revision": revision,
                    "bench_start_body": start_body,
                    "bench_project": project,
                },
                report_path,
                samples,
            )
            return
        # Idle selections are immutable and do not pretend to be process confirmations.
        config_file = c["root"] / "settings.json"
        config_file.write_text('{"sample_interval_ms": 150}')
        saved = cli(
            "settings", "set", installed["installation_id"], "--file", config_file
        )
        assert saved["state"] == "saved_for_next_start"
        assert (
            cli("settings", "show", installed["installation_id"])["last_applied"]
            is None
        )
        first = start()
        wait(lambda: applied(150), "initial settings applied")
        wait(
            lambda: any(x.get("startup_sample_count") == "2" for x in output(first)),
            "literal startup environment mapping",
        )
        assert write(99).status_code == 400
        before = settings()
        key = str(uuid.uuid4())
        response = checked(write(250, key, before))
        assert response["state"] == "pending"
        assert checked(write(250, key, before)) == response
        assert write(350, key, before).status_code == 409
        wait(lambda: applied(250), "active settings applied")
        selected = settings()["selected_for_next_start"]["id"]
        rejected = checked(write(777))
        wait(
            lambda: settings()["pending_application"] is None, "App rejection reported"
        )
        rejection = checked(
            client.get("/v1/app-operations/" + rejected["application_id"])
        )
        assert rejection["state"] == "failed"
        assert settings()["last_applied"]["values"]["sample_interval_ms"] == 250
        assert settings()["selected_for_next_start"]["id"] == selected
        assert write(400, state=before).status_code == 409

        # Real CLI/HTTP authorization with separate signed test principals.
        operator_token = signed_token(c["pem"])
        with httpx.Client(
            base_url=str(client.base_url),
            headers={"Authorization": "Bearer " + operator_token},
        ) as operator:
            user = checked(operator.get("/v1/me"))["user"]["id"]
            checked(
                client.post(
                    f"/v1/organizations/{c['org']}/members",
                    json={"user_id": user, "role": "operator"},
                )
            )
            assert operator.get(settings_path).status_code == 200
            assert operator.post(
                settings_path, headers={"Idempotency-Key": str(uuid.uuid4())}, json={}
            ).status_code in (400, 403)
            state = settings()
            body = {
                "values": {"sample_interval_ms": 300},
                "expected_selected_revision_id": state["selected_for_next_start"]["id"],
                "expected_applied_revision_id": state["last_applied"]["id"],
                "expected_run_id": first["run_id"],
            }
            assert (
                operator.post(
                    settings_path,
                    headers={"Idempotency-Key": str(uuid.uuid4())},
                    json=body,
                ).status_code
                == 403
            )
        with httpx.Client(
            base_url=str(client.base_url),
            headers={"Authorization": "Bearer " + signed_token(c["pem"])},
        ) as stranger:
            assert stranger.get(settings_path).status_code == 404
            assert (
                stranger.get(
                    f"/v1/apps/{revision['app_id']}/artifacts",
                    params={"organization_id": c["org"]},
                ).status_code
                == 404
            )

        # Crash API before local result capture; lose the App's result ACK after capture.
        faults.update(api=api, crash_api=True, drop_result=True)
        accepted = checked(write(450))
        assert faults["dropped"].wait(30), "result response fault not reached"
        application = accepted["application_id"]
        with connect(c["db"]) as db:
            row = db.execute(
                "SELECT outcome,delivered_at FROM app_settings_applications WHERE application_id=?",
                (application,),
            ).fetchone()
            assert row == ("applied", None), row
        c["support"].terminate(daemon)
        daemon = c["start_daemon"]()
        processes.append(daemon)
        time.sleep(1)
        api = c["start_api"]()
        processes.append(api)
        wait(
            lambda: client.get("/health/ready").status_code == 200,
            "API replacement ready",
        )
        wait(
            lambda: c["status"]()["connectivity"] == "online",
            "daemon replacement connected",
        )
        faults["pause_results"] = False
        wait(lambda: applied(450), "durable result delivered after replacement")
        wait(
            lambda: any(
                x.get("event") == "settings.handler"
                and x.get("sample_interval_ms") == 450
                for x in output(first)
            ),
            "handler output delivered after replacement",
        )
        assert run(first)["state"] == "running"

        # Stop with a durable but unacknowledged application; later run must not inherit it.
        faults["pause_results"] = True
        pending = checked(write(650))
        assert pending["run_id"] == first["run_id"]
        assert write(700).status_code == 409
        stop(first)
        wait(
            lambda: checked(
                client.get(
                    "/v1/app-runs/" + first["run_id"] + "/output",
                    params={"stream": "stdout", "offset": 0},
                )
            )["sealed"],
            "first output sealed",
        )
        assert (
            sum(
                x.get("event") == "settings.handler"
                and x.get("sample_interval_ms") == 450
                for x in output(first)
            )
            == 1
        )
        wait(
            lambda: settings()["pending_application"] is None,
            "ended run clears pending settings",
        )
        faults["pause_results"] = False
        second = start()
        wait(lambda: applied(450), "later run retains confirmed selection")
        assert not any(x.get("sample_interval_ms") == 650 for x in output(second))
        stop(second)

        # Same relative path across runs retains distinct bytes, identities and cursor binding.
        files_path = f"/v1/apps/{revision['app_id']}/artifacts"
        params = {"organization_id": c["org"], "path_prefix": "reports/", "limit": 1}
        page = wait(
            lambda: (
                checked(client.get(files_path, params=params))
                if len(
                    checked(
                        client.get(files_path, params={"organization_id": c["org"]})
                    )["items"]
                )
                == 2
                else False
            ),
            "two run summaries published",
        )
        assert page["next_cursor"] and len(page["items"]) == 1
        later = checked(
            client.get(files_path, params=params | {"cursor": page["next_cursor"]})
        )
        items = page["items"] + later["items"]
        assert len({x["run_id"] for x in items}) == 2
        assert {x["path"] for x in items} == {"reports/settings.json"}
        assert len({x["sha256"] for x in items}) == 2
        assert (
            client.get(
                files_path,
                params=params
                | {"cursor": page["next_cursor"], "path_prefix": "other/"},
            ).status_code
            == 400
        )
        for item in items:
            destination = c["root"] / (item["artifact_id"] + ".json")
            fetched = subprocess.run(
                [
                    str(ROOT / "apps/cli/.venv/bin/ringfour-cli"),
                    "app",
                    "artifact",
                    item["run_id"],
                    item["artifact_id"],
                    "--output",
                    str(destination),
                ],
                env=c["cli_env"],
                capture_output=True,
                text=True,
                check=False,
            )
            assert fetched.returncode == 0, fetched.stdout + fetched.stderr
            assert json.loads(destination.read_text())["values"][
                "sample_interval_ms"
            ] in (450, 650)
        history = checked(
            client.get(
                f"/v1/organizations/{c['org']}/app-logbook",
                params={"installation_id": installed["installation_id"]},
            )
        )["items"]
        assert len({x["event_id"] for x in history}) == len(history)
        assert any(x["type"] == "app.settings.applied" for x in history)
        assert any(x["type"] == "app.settings.rejected" for x in history)
        evidence = {
            "ap17_passed": True,
            "manifest_v2": True,
            "startup_environment": True,
            "initial_and_live_settings": True,
            "rejection_and_cas": True,
            "api_daemon_replacement": True,
            "lost_result_ack_no_reapply": True,
            "stop_pending_no_cross_run_delivery": True,
            "files_cli_and_scope": True,
            "test_principals_not_live_auth0": True,
            "organization_id": c["org"],
            "robot_id": c["robot"],
            "app_id": revision["app_id"],
            "revision_id": revision["id"],
            "manifest_digest": revision["manifest_digest"],
            "image": revision["manifest"]["targets"][0]["image"],
            "wheel_bundle": authoring["wheel_bundle"],
            "run_ids": [first["run_id"], second["run_id"]],
            "application_ids": {
                "recovered": application,
                "rejected": rejected["application_id"],
                "ended_pending": pending["application_id"],
            },
            "operator_user_id": user,
            "publisher_user_id": checked(client.get("/v1/me"))["user"]["id"],
            "artifact_ids": [item["artifact_id"] for item in items],
        }
        if evidence_path:
            evidence_path.parent.mkdir(parents=True, exist_ok=True)
            evidence_path.write_text(json.dumps(evidence, indent=2) + "\n")
        print(json.dumps(evidence), flush=True)
        if report_path:
            load("ap19-backend").benchmark(
                c
                | {
                    "daemon": daemon,
                    "api": api,
                    "bench_resource": resource,
                    "bench_revision": revision,
                    "bench_start_body": start_body,
                    "bench_project": project,
                },
                report_path,
                samples,
            )
    finally:
        faults["pause_results"] = False
        for accepted in started:
            try:
                if run(accepted)["state"] in ("accepted", "running"):
                    stop(accepted)
            except (httpx.HTTPError, AssertionError):
                pass
        for process in reversed(processes):
            c["support"].terminate(process)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--backend-report", type=Path)
    parser.add_argument("--evidence-report", type=Path)
    parser.add_argument("--samples", type=int, default=20)
    parser.add_argument("--benchmark-only", action="store_true")
    args = parser.parse_args()
    faults = {"dropped": threading.Event()}
    if args.backend_report:
        os.environ["AP_TEST_PG_STATS"] = "true"
        os.environ.setdefault("AP_TEST_API_SWEEP_MS", "2000")
    load("ap07-install").main(
        lambda c: acceptance(
            c,
            faults,
            args.backend_report,
            args.samples,
            args.benchmark_only,
            args.evidence_report,
        ),
        lambda c: configure(c, faults),
    )


if __name__ == "__main__":
    main()
