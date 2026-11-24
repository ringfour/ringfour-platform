#!/usr/bin/env python3
"""Actual App runtime clients -> Daemon -> API, with no Runner and durable evidence."""

import base64
import importlib.util
import json
import subprocess
import threading
import uuid
from pathlib import Path

import httpx
from ringfour_daemon.db import connect


def configure(c):
    support = c["support"]
    gateway = json.loads(
        support.command(
            ["docker", "--host", c["docker_host"], "network", "inspect", c["network"]]
        )
    )[0]["IPAM"]["Config"][0]["Gateway"]
    relay = c["engine"] + "-runtime-relay"
    # A fixture-only stdio relay avoids assuming Docker's host gateway is this
    # process's host (Docker Desktop/WSL and remote engines differ).
    script = r"""import http.server,threading,queue,json,base64,sys,uuid
pending={}
lock=threading.Lock()
class Handler(http.server.BaseHTTPRequestHandler):
 def log_message(self,*args):pass
 def handle_request(self):
  identifier=str(uuid.uuid4());response=queue.Queue()
  with lock:pending[identifier]=response
  body=self.rfile.read(int(self.headers.get('Content-Length',0)))
  with lock:print(json.dumps({'id':identifier,'method':self.command,'path':self.path,'headers':dict(self.headers),'body':base64.b64encode(body).decode()}),flush=True)
  try:
   value=response.get(timeout=65);data=base64.b64decode(value['body'])
   self.send_response(value['status'])
   self.send_header('Content-Type',value.get('content_type','application/json'))
   self.send_header('Content-Length',str(len(data)));self.end_headers();self.wfile.write(data)
  finally:
   with lock:pending.pop(identifier,None)
 do_POST=handle_request
 do_GET=handle_request
 do_PUT=handle_request
 def end_headers(self):super().end_headers()
def receive():
 for line in sys.stdin:
  value=json.loads(line)
  with lock:target=pending.get(value['id'])
  if target:target.put(value)
threading.Thread(target=receive,daemon=True).start()
http.server.ThreadingHTTPServer(('0.0.0.0',8081),Handler).serve_forever()
"""
    process = subprocess.Popen(
        [
            "docker",
            "run",
            "--rm",
            "-i",
            "--name",
            relay,
            "--network",
            "container:" + c["engine"],
            "--entrypoint",
            "python",
            "ringfour-daemon:local",
            "-u",
            "-c",
            script,
        ],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        bufsize=1,
    )

    def forward():
        with httpx.Client(
            base_url=f"http://127.0.0.1:{c['daemon_port']}", timeout=60, trust_env=False
        ) as client:
            for line in process.stdout:
                value = json.loads(line)
                try:
                    reply = client.request(
                        value["method"],
                        value["path"],
                        headers={
                            k: v
                            for k, v in value["headers"].items()
                            if k.lower() not in ("host", "content-length", "connection")
                        },
                        content=base64.b64decode(value["body"]),
                    )
                    result = {
                        "id": value["id"],
                        "status": reply.status_code,
                        "content_type": reply.headers.get(
                            "content-type", "application/json"
                        ),
                        "body": base64.b64encode(reply.content).decode(),
                    }
                except httpx.HTTPError:
                    result = {
                        "id": value["id"],
                        "status": 503,
                        "body": base64.b64encode(b"{}").decode(),
                    }
                try:
                    process.stdin.write(json.dumps(result) + "\n")
                    process.stdin.flush()
                except (BrokenPipeError, ValueError):
                    return

    threading.Thread(target=forward, daemon=True).start()
    c["daemon_env"]["RINGFOUR_APP_RUNTIME_URL"] = f"http://{gateway}:8081"

    def cleanup():
        subprocess.run(["docker", "rm", "-f", relay], capture_output=True, check=False)
        process.wait(timeout=10)

    return cleanup


def acceptance(c):
    client, checked, post, support, cli = [
        c[k] for k in ("client", "checked", "post", "support", "cli")
    ]
    project = c["root"] / "runtime-fixture"
    cli(
        "init",
        project,
        "--app-key",
        "runtime-fixture",
        "--repository",
        c["host"] + "/apps/runtime-fixture",
    )
    (project / "main.py").write_text(
        (Path(__file__).parents[1] / "fixtures/apps/runtime/main.py").read_text()
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
    c["fault"]["armed"] = True
    c["fault"]["artifact"] = True
    accepted = checked(
        post(
            resource + "/start",
            {
                "expected_revision_id": binding["revision_id"],
                "expected_configuration_revision_id": binding[
                    "configuration_revision_id"
                ],
            },
        )
    )
    run = accepted["run_id"]

    def ready():
        with connect(c["db"]) as db:
            row = db.execute(
                "SELECT stdout,stderr,state,failure_json FROM app_runs WHERE run_id=?",
                (run,),
            ).fetchone()
        if row and row[2] in ("failed", "interrupted"):
            raise AssertionError(
                "Runtime App failed: "
                + str(row[3])
                + " "
                + bytes(row[1]).decode(errors="replace")
            )
        if row:
            for line in bytes(row[0]).decode(errors="replace").splitlines():
                try:
                    value = json.loads(line)
                    if value.get("event") == "runtime.ready":
                        return value
                except ValueError:
                    pass
        return None

    ready_event = support.wait_for(
        ready, "actual runtime clients create artifact/logbook/HITL", timeout=90
    )
    request = ready_event["request_id"]
    with connect(c["db"]) as db:
        assert db.execute(
            "SELECT delivered FROM app_artifact_spool WHERE run_id=?", (run,)
        ).fetchone() == (0,)
    assert not c["fault"][
        "armed"
    ]  # API was interrupted during actual archive snapshot.
    support.terminate(c["daemon"])
    recovered_api = c["start_api"]()
    support.wait_for(
        lambda: client.get("/health/ready").status_code == 200,
        "API restored after artifact snapshot",
    )
    first_replacement = c["start_daemon"]()
    interaction_path = f"/v1/app-runs/{run}/interactions/{request}"
    support.wait_for(
        lambda: client.get(interaction_path).status_code == 200,
        "hosted interaction after offline spool recovery",
    )
    support.terminate(first_replacement)
    # Human submission is provisional while the Daemon is stopped.
    reply = checked(post(interaction_path + "/responses", {"chosen_value": "yes"}))
    assert reply["response"]["status"] == "submitted"
    assert (
        post(interaction_path + "/responses", {"chosen_value": "no"}).status_code == 409
    )
    wrong = client.get(f"/v1/app-runs/{uuid.uuid4()}/interactions/{request}")
    assert wrong.status_code == 404
    replacement = c["start_daemon"]()
    try:
        support.wait_for(
            lambda: (
                checked(client.get(interaction_path))["response"]["status"] == "applied"
            ),
            "submitted answer applied after Daemon replacement",
            timeout=90,
        )
        support.wait_for(
            lambda: checked(client.get(f"/v1/app-runs/{run}"))["state"] == "succeeded",
            "actual App exits successfully",
        )

        def output():
            value = checked(
                client.get(f"/v1/app-runs/{run}/output", params={"stream": "stdout"})
            )
            return value if value["sealed"] else None

        delivered = support.wait_for(output, "sealed output delivered")
        assert b"runtime.answered" in base64.b64decode(delivered["data_base64"])
        artifact_path = f"/v1/app-runs/{run}/artifacts/{ready_event['artifact_id']}"
        support.wait_for(
            lambda: client.get(artifact_path).status_code == 200, "artifact upload"
        )
        assert (
            client.get(artifact_path + "/content").content
            == b"original immutable evidence"
        )
        history = checked(
            client.get(
                f"/v1/organizations/{c['org']}/app-logbook", params={"run_id": run}
            )
        )["items"]
        support.wait_for(
            lambda: any(
                x["type"] == "app.logbook.entry"
                for x in checked(
                    client.get(
                        f"/v1/organizations/{c['org']}/app-logbook",
                        params={"run_id": run},
                    )
                )["items"]
            ),
            "authored entry delivered",
        )
        history = checked(
            client.get(
                f"/v1/organizations/{c['org']}/app-logbook", params={"run_id": run}
            )
        )["items"]
        types = {x["type"] for x in history}
        assert {
            "app.run.started",
            "app.run.completed",
            "app.logbook.entry",
            "human.interaction.requested",
            "human.interaction.answered",
        } <= types
        scoped = checked(
            client.get(
                f"/v1/organizations/{c['org']}/app-logbook",
                params={"installation_id": install["installation_id"]},
            )
        )["items"]
        assert {x["event_id"] for x in history} <= {x["event_id"] for x in scoped}
        with connect(c["db"]) as db:
            assert db.execute(
                "SELECT count(*) FROM app_artifact_spool WHERE run_id=? AND delivered=1",
                (run,),
            ).fetchone() == (1,)
        print(
            json.dumps(
                {
                    "ap10_passed": True,
                    "runtime_clients_without_runner": True,
                    "immutable_artifact_after_source_delete": True,
                    "sealed_output": True,
                    "submitted_then_applied_after_replacement": True,
                    "shared_history": True,
                    "offline_artifact_spool_recovery": True,
                }
            ),
            flush=True,
        )
    finally:
        support.terminate(replacement)
        support.terminate(recovered_api)


def main():
    spec = importlib.util.spec_from_file_location(
        "install_support", Path(__file__).with_name("ap07-install.py")
    )
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.main(acceptance, configure)


if __name__ == "__main__":
    main()
