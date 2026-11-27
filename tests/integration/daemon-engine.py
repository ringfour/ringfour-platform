#!/usr/bin/env python3
"""Actual Docker owner/recovery protocol acceptance in a disposable nested engine."""

import json
import os
import select
import socket
import socketserver
import subprocess
import sys
import tempfile
import threading
import time
import uuid
from pathlib import Path

import httpx
from ringfour_daemon.app_engine import Engine, EngineError, Executor
from ringfour_daemon.config import Settings
from ringfour_daemon.db import connect, migrate

ROOT = Path(__file__).resolve().parents[2]
DIND = "docker:29-dind@sha256:4bbc7223f81db790097c6dea42fb93e05b8fead4f0431fbe6ccaba6e8473201c"
REGISTRY = (
    "registry:3@sha256:82f6ff4a7f982960c8aa51617fea3aabc485df6e55cce80d22deb1de36f1bc3c"
)


def command(args, **kwargs):
    return subprocess.check_output(args, text=True, **kwargs).strip()


def daemon_replacement(root, settings, engine_url, container_id):
    # A fixture-only Unix/TCP bridge lets actual Daemon processes use the same
    # Unix socket boundary as deployments, with only the isolated nested engine.
    port = int(engine_url.rsplit(":", 1)[1])

    class Proxy(socketserver.BaseRequestHandler):
        def handle(self):
            with socket.create_connection(("127.0.0.1", port)) as remote:
                while True:
                    readable, _, _ = select.select([self.request, remote], [], [], 3)
                    for source in readable:
                        data = source.recv(65536)
                        if not data:
                            return
                        (remote if source is self.request else self.request).sendall(
                            data
                        )

    class Server(socketserver.ThreadingUnixStreamServer):
        daemon_threads = True

    path = str(root / "engine.sock")
    server = Server(path, Proxy)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    credential = root / "credential"
    credential.write_text("ap08-disposable")
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        api_port = sock.getsockname()[1]
    env = os.environ | {
        "DAEMON_DB_PATH": settings.db_path,
        "DAEMON_CREDENTIAL_PATH": str(credential),
        "RINGFOUR_APPS_ENABLED": "true",
        "RINGFOUR_ENGINE_SOCKET": path,
        "RINGFOUR_APP_NETWORK": settings.app_network,
        "RINGFOUR_AGENT_URI": "",
        "PLATFORM_API_URL": "http://127.0.0.1:1",
    }
    try:
        for attempt in range(2):
            with (root / f"daemon-{attempt}.log").open("w") as log:
                process = subprocess.Popen(
                    [
                        sys.executable,
                        "-m",
                        "uvicorn",
                        "ringfour_daemon.main:create_app",
                        "--factory",
                        "--host",
                        "127.0.0.1",
                        "--port",
                        str(api_port),
                    ],
                    env=env,
                    stdout=log,
                    stderr=subprocess.STDOUT,
                )
                try:
                    for _ in range(100):
                        assert process.poll() is None, (
                            root / f"daemon-{attempt}.log"
                        ).read_text()
                        try:
                            ready = httpx.get(
                                f"http://127.0.0.1:{api_port}/health/ready", timeout=2
                            )
                            if ready.status_code == 200:
                                break
                        except httpx.HTTPError:
                            pass
                        time.sleep(0.1)
                    else:
                        raise AssertionError("Daemon readiness timed out")
                    assert ready.json()["checks"] == {"sqlite": "ok", "engine": "ok"}
                    time.sleep(1.2)
                    with connect(settings.db_path) as conn:
                        assert conn.execute(
                            "SELECT container_id,state FROM app_runs"
                        ).fetchone() == (container_id, "running")
                finally:
                    process.terminate()
                    try:
                        process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=5)
    finally:
        server.shutdown()
        server.server_close()


def main():
    name = "ap08-engine-" + uuid.uuid4().hex[:10]
    registry = name + "-registry"
    executor = None
    with tempfile.TemporaryDirectory(prefix="ap08-") as directory:
        root = Path(directory)
        try:
            command(
                [
                    "docker",
                    "run",
                    "-d",
                    "--rm",
                    "--privileged",
                    "--name",
                    name,
                    "-e",
                    "DOCKER_TLS_CERTDIR=",
                    "-p",
                    "127.0.0.1::2375",
                    DIND,
                    "--storage-driver=vfs",
                    "--iptables=false",
                ]
            )
            inspect = json.loads(command(["docker", "inspect", name]))[0]
            port = inspect["NetworkSettings"]["Ports"]["2375/tcp"][0]["HostPort"]
            url = "http://127.0.0.1:" + port
            for _ in range(150):
                try:
                    if httpx.get(url + "/_ping", timeout=1).status_code == 200:
                        break
                except httpx.HTTPError:
                    pass
                time.sleep(0.2)
            command(
                [
                    "docker",
                    "run",
                    "-d",
                    "--rm",
                    "--name",
                    registry,
                    "--network",
                    "container:" + name,
                    REGISTRY,
                ]
            )
            docker = ["docker", "--host", "tcp://127.0.0.1:" + port]
            # Existing local dependency only; no infrastructure image rebuild.
            with (root / "base.tar").open("wb") as stream:
                subprocess.run(
                    ["docker", "save", "alpine:3.20"], stdout=stream, check=True
                )
            command([*docker, "load", "-i", str(root / "base.tar")])
            (root / "Dockerfile").write_text(
                'FROM alpine:3.20\nWORKDIR /app\nCOPY main.sh /app/main.sh\nUSER 10001:10001\nENTRYPOINT ["/bin/sh","/app/main.sh"]\n'
            )
            (root / "main.sh").write_text(
                'cat /app/ringfour-config.json\nprintf "ready\\n"\nprintf "stderr evidence\\n" >&2\nsleep 120\n'
            )
            tag = "127.0.0.1:5000/apps/recovery:v1"
            command(
                [*docker, "build", "-t", tag, str(root)],
                env=os.environ | {"DOCKER_BUILDKIT": "0"},
            )
            command([*docker, "push", tag])
            image = json.loads(command([*docker, "image", "inspect", tag]))[0]
            reference = image["RepoDigests"][0]
            network = command([*docker, "network", "create", "ap08-owned"])
            db = str(root / "daemon.db")
            migrate(db)
            robot, namespace, installation, run, operation, app, revision, config = [
                str(uuid.uuid4()) for _ in range(8)
            ]
            now = "2026-11-23T12:00:00Z"
            with connect(db) as c:
                c.execute(
                    "INSERT INTO device_identity VALUES (1,?,?,?,?)",
                    (robot, str(uuid.uuid4()), "http://unused", str(uuid.uuid4())),
                )
                c.execute(
                    "INSERT INTO device_deployment_namespace VALUES (1,?,?)",
                    (namespace, now),
                )
                c.execute(
                    "INSERT INTO device_operations VALUES (?,?,'app.start',1,'{}','accepted',?,?,NULL)",
                    (operation, robot, "2099-01-01T00:00:00Z", now),
                )
                c.execute(
                    "INSERT INTO app_installations(installation_id,robot_id,app_id,updated_at) VALUES (?,?,?,?)",
                    (installation, robot, app, now),
                )
                c.execute(
                    "INSERT INTO app_runs(run_id,installation_id,robot_id,operation_id,app_id,revision_id,image_digest,configuration_revision_id,snapshot_json,state,requested_at,updated_at) VALUES (?,?,?,?,?,?,?,?,?,'accepted',?,?)",
                    (
                        run,
                        installation,
                        robot,
                        operation,
                        app,
                        revision,
                        reference,
                        config,
                        json.dumps(
                            {"image": reference, "configuration": {"sample_count": 2}}
                        ),
                        now,
                        now,
                    ),
                )
            settings = Settings(
                db_path=db,

                apps_enabled=True,
                app_network=network,
            )

            def engine():
                result = Engine(settings)
                result.client.close()
                result.client = httpx.Client(base_url=url, timeout=120)
                return result

            executor = Executor(settings, engine()).open()
            target = {"image": reference, "os": "linux", "architecture": "amd64"}
            executor.engine.pull(target, {"kind": "anonymous"})
            assert (
                executor.engine.owned(robot, namespace) == []
            )  # Pull creates no workload.
            original = executor.engine.request

            def lost_create(method, path, **kwargs):
                response = original(method, path, **kwargs)
                if path == "/containers/create":
                    raise EngineError()
                return response

            executor.engine.request = lost_create
            try:
                executor.create(run)
                raise AssertionError("Expected lost create response")
            except EngineError:
                pass
            assert executor.row(run)[0]["container_id"] is None
            executor.close()
            executor = Executor(settings, engine()).open()
            row = executor.create(run)
            container_id = row["container_id"]
            original = executor.engine.request

            def lost_start(method, path, **kwargs):
                response = original(method, path, **kwargs)
                if path.endswith("/start"):
                    raise EngineError()
                return response

            executor.engine.request = lost_start
            try:
                executor.start(run)
                raise AssertionError("Expected lost start response")
            except EngineError:
                pass
            executor.close()
            executor = Executor(settings, engine()).open()
            assert executor.start(run)["container_id"] == container_id
            assert executor.row(run)[0]["state"] == "running"
            inspected = executor.engine.container(container_id)
            assert inspected["Config"]["User"] == "10001:10001"
            assert not inspected["HostConfig"]["Privileged"] and not inspected["Mounts"]
            assert len(executor.engine.owned(robot, namespace)) == 1
            started_at = inspected["State"]["StartedAt"]
            executor.close()
            daemon_replacement(root, settings, url, container_id)
            executor = Executor(settings, engine()).open()
            assert (
                executor.engine.container(container_id)["State"]["StartedAt"]
                == started_at
            )
            assert len(executor.engine.owned(robot, namespace)) == 1
            row = executor.stop(run)
            assert row["state"] == "stopped"
            assert (
                b'"sample_count":2' in row["stdout"]
                and b"stderr evidence" in row["stderr"]
            )
            assert row["stdout_sealed"] == row["stderr_sealed"] == 1
            executor.remove(run)
            assert executor.engine.container(container_id) is None
            assert executor.start(run)["state"] == "stopped"
            print(
                json.dumps(
                    {
                        "passed": True,
                        "engine_api": "1.46",
                        "image": reference,
                        "create_before_record_recovery": True,
                        "replacement_no_relaunch": True,
                        "actual_daemon_replacement": True,
                        "output_durable_before_remove": True,
                        "no_container_on_pull": True,
                    }
                )
            )
        finally:
            if executor:
                executor.close()
            subprocess.run(
                ["docker", "rm", "-f", "-v", registry, name],
                capture_output=True,
                check=False,
            )


if __name__ == "__main__":
    main()
