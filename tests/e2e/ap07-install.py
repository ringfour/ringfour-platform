#!/usr/bin/env python3
"""Real CLI/Agent/Daemon/Engine/Kestrel/PG install/config/update/rollback acceptance."""

import importlib.util
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
from ringfour_daemon.db import connect

ROOT = Path(__file__).resolve().parents[2]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / "tests/e2e" / path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main(run_acceptance=None, configure=None):
    support = load("ap03_support", "ap03-inventory.py")
    catalog = load("ap05_support", "ap05-catalog.py")
    publisher = load("ap06_support", "ap06-publish.py")
    prefix = "ap07-" + uuid.uuid4().hex[:10]
    pg, registry, engine, agent = [
        prefix + "-" + name for name in ("pg", "registry", "engine", "agent")
    ]
    api = daemon = None
    server = None
    logs = []
    cleanup = None
    with tempfile.TemporaryDirectory(prefix="ap07-install-") as directory:
        root = Path(directory)
        try:
            support.command(
                [
                    "dotnet",
                    "build",
                    "apps/api/src/Ringfour.Api/Ringfour.Api.csproj",
                    "--no-restore",
                    "--verbosity",
                    "quiet",
                ]
            )
            registry_port = support.port()
            support.command(
                [
                    "docker",
                    "run",
                    "--rm",
                    "-d",
                    "--name",
                    pg,
                    "-e",
                    "POSTGRES_USER=ap07",
                    "-e",
                    "POSTGRES_PASSWORD=ap07-disposable",
                    "-e",
                    "POSTGRES_DB=ap07",
                    "-p",
                    "127.0.0.1::5432",
                    "postgres:18",
                ]
            )
            support.command(
                [
                    "docker",
                    "run",
                    "--rm",
                    "-d",
                    "--privileged",
                    "--name",
                    engine,
                    "-e",
                    "DOCKER_TLS_CERTDIR=",
                    "-p",
                    "127.0.0.1::2375",
                    "-p",
                    f"127.0.0.1:{registry_port}:{registry_port}",
                    publisher.DIND,
                    "--storage-driver=vfs",
                    "--iptables=false",
                ]
            )

            def port(name, target):
                return int(
                    json.loads(support.command(["docker", "inspect", name]))[0][
                        "NetworkSettings"
                    ]["Ports"][target][0]["HostPort"]
                )

            engine_port = port(engine, "2375/tcp")
            engine_url = f"http://127.0.0.1:{engine_port}"
            docker_host = f"tcp://127.0.0.1:{engine_port}"
            support.wait_for(
                lambda: httpx.get(engine_url + "/_ping").status_code == 200,
                "isolated engine",
            )
            support.command(
                [
                    "docker",
                    "run",
                    "--rm",
                    "-d",
                    "--name",
                    registry,
                    "--network",
                    "container:" + engine,
                    "-e",
                    f"REGISTRY_HTTP_ADDR=0.0.0.0:{registry_port}",
                    catalog.REGISTRY,
                ]
            )
            host = f"127.0.0.1:{registry_port}"
            # Actual pinned Agent implementation/profile, with ROS disabled; no sensor success claimed.
            profile = support.command(
                [
                    "git",
                    "-C",
                    str(ROOT.parent / "ringfour-virtual/dependencies/base101"),
                    "show",
                    "1f1a4004ac7dfb2257b8ce7a6e40fa4a9a795ece:profiles/base101.yaml",
                ]
            )
            (root / "profile.yaml").write_text(profile)
            (root / "agent.py").write_text("""import asyncio,tempfile
from ringfour_agent.bootstrap import build_agent
from ringfour_agent.profile import load_profile
async def main():
    with tempfile.TemporaryDirectory() as directory:
        server,ctx=build_agent(load_profile('/acceptance/profile.yaml'),unix_path=None,host='0.0.0.0',port=10101,audit_dir=directory,ros=None)
        await server.start()
        try: await asyncio.Event().wait()
        finally: await server.stop();ctx.audit.close()
asyncio.run(main())
""")
            support.command(
                [
                    "docker",
                    "run",
                    "--rm",
                    "-d",
                    "--name",
                    agent,
                    "--mount",
                    f"type=bind,src={root},dst=/acceptance,readonly",
                    "--mount",
                    f"type=bind,src={ROOT.parent / 'ringfour-agent'},dst=/agent-src,readonly",
                    "--mount",
                    f"type=bind,src={ROOT.parent / 'ringfour-sdk'},dst=/sdk-src,readonly",
                    "-p",
                    "127.0.0.1::10101",
                    "--entrypoint",
                    "/bin/bash",
                    "ringfour-agent:local",
                    "-c",
                    "source /opt/ringfour/setup.bash; export PYTHONPATH=/agent-src/src/ringfour_agent:/sdk-src:${PYTHONPATH:-}; exec python3 /acceptance/agent.py",
                ]
            )
            agent_uri = f"ws://127.0.0.1:{port(agent, '10101/tcp')}"
            api_port, daemon_port = support.port(), support.port()
            jwt, pem = support.token_and_key()
            api_env = os.environ | {
                "ASPNETCORE_ENVIRONMENT": "Development",
                "ASPNETCORE_URLS": f"http://127.0.0.1:{api_port}",
                "ConnectionStrings__Postgres": f"Host=127.0.0.1;Port={port(pg, '5432/tcp')};Database=ap07;Username=ap07;Password=ap07-disposable",
                "Auth__Test__Issuer": "https://ap03.test/",
                "Auth__Test__Audience": "https://api.ringfour.eu",
                "Auth__Test__RsaPrivateKeyPem": pem,
                "Storage__Backend": "filesystem",
                "Storage__FilesystemRoot": str(root / "objects"),
                "Apps__InstallationsEnabled": "true",
                "Apps__InstallationSweepMilliseconds": "300",
                "Apps__Registries__0__Host": host,
                "Apps__Registries__0__RepositoryPrefix": "apps",
                "Apps__Registries__0__AllowHttp": "true",
                "Apps__Registries__0__AnonymousPull": "true",
            }

            def start_api():
                log = (root / f"api-{len(logs)}.log").open("w+")
                logs.append(log)
                return subprocess.Popen(
                    [
                        "dotnet",
                        str(
                            ROOT
                            / "apps/api/src/Ringfour.Api/bin/Debug/net10.0/Ringfour.Api.dll"
                        ),
                    ],
                    cwd=ROOT,
                    env=api_env,
                    stdout=log,
                    stderr=subprocess.STDOUT,
                )

            api = start_api()
            with httpx.Client(
                base_url=f"http://127.0.0.1:{api_port}",
                headers={"Authorization": "Bearer " + jwt},
                timeout=30,
                trust_env=False,
            ) as client:
                support.wait_for(
                    lambda: client.get("/health/ready").status_code == 200,
                    "API readiness",
                )

                def checked(response):
                    response.raise_for_status()
                    return response.json()

                org = checked(
                    client.post("/v1/organizations", json={"name": "AP07 publisher"})
                )["id"]
                robot = checked(
                    client.post(
                        f"/v1/organizations/{org}/robots",
                        json={"name": "AP07 installation robot"},
                    )
                )["id"]
                enrollment = checked(
                    client.post(f"/v1/robots/{robot}/enrollment-tokens")
                )["token"]
                config = root / "cli-config.json"
                config.write_text(
                    json.dumps(
                        {
                            "api_url": str(client.base_url),
                            "auth0_domain": "ap07.example.invalid",
                            "auth0_client_id": "ap07-test",
                            "selected_org": org,
                        }
                    )
                )
                cli_env = os.environ | {
                    "RINGFOUR_CONFIG": str(config),
                    "RINGFOUR_API_URL": str(client.base_url),
                    "RINGFOUR_AUTH0_DOMAIN": "ap07.example.invalid",
                    "RINGFOUR_AUTH0_CLIENT_ID": "ap07-test",
                    "RINGFOUR_AUTH0_AUDIENCE": "https://api.ringfour.eu",
                    "DOCKER_HOST": docker_host,
                    "PYTHON_KEYRING_BACKEND": "env_keyring.EnvironmentKeyring",
                    "RINGFOUR_TEST_TOKEN_JSON": json.dumps(
                        {"access_token": jwt, "expires_at": time.time() + 600}
                    ),
                    "PYTHONPATH": str(ROOT / "apps/cli/tests")
                    + os.pathsep
                    + str(ROOT / "apps/cli/src"),
                }

                def cli(*args):
                    result = subprocess.run(
                        [
                            str(ROOT / "apps/cli/.venv/bin/ringfour-cli"),
                            "app",
                            *map(str, args),
                            "--json",
                        ],
                        cwd=root,
                        env=cli_env,
                        capture_output=True,
                        text=True,
                        timeout=600,
                        check=False,
                    )
                    assert result.returncode == 0, result.stdout + result.stderr
                    return json.loads(result.stdout)

                project = root / "robot-check"
                cli(
                    "init",
                    project,
                    "--app-key",
                    "ap07-check",
                    "--repository",
                    host + "/apps/robot-check",
                )
                cli("build", project)
                revision = cli("publish", project)
                manifest = json.loads((project / "app.manifest.json").read_text())
                image = manifest["targets"][0]["image"]
                network = support.command(
                    ["docker", "--host", docker_host, "network", "create", "ap07-owned"]
                )
                fault = {"armed": False}
                requests = []

                class Proxy(socketserver.BaseRequestHandler):
                    def handle(self):
                        nonlocal api
                        with socket.create_connection(
                            ("127.0.0.1", engine_port)
                        ) as remote:
                            while True:
                                readable, _, _ = select.select(
                                    [self.request, remote], [], [], 3
                                )
                                for source in readable:
                                    data = source.recv(65536)
                                    if not data:
                                        return
                                    if source is self.request and data.startswith(
                                        (b"GET ", b"POST ", b"PUT ", b"DELETE ")
                                    ):
                                        request = data.split(b"\r\n", 1)[0].decode()
                                        requests.append(request)
                                        if fault["armed"] and (
                                            request.startswith(
                                                "POST /v1.46/images/create"
                                            )
                                            or (
                                                fault.get("artifact")
                                                and request.startswith("GET ")
                                                and "/archive?" in request
                                            )
                                        ):
                                            fault["armed"] = False
                                            support.terminate(api)
                                            api = None
                                    (
                                        remote
                                        if source is self.request
                                        else self.request
                                    ).sendall(data)

                class Server(socketserver.ThreadingUnixStreamServer):
                    daemon_threads = True

                socket_path = str(root / "engine.sock")
                server = Server(socket_path, Proxy)
                threading.Thread(target=server.serve_forever, daemon=True).start()
                db = str(root / "daemon.db")
                daemon_env = os.environ | {
                    "PYTHONPATH": str(ROOT / "apps/daemon/src"),
                    "DAEMON_DB_PATH": db,
                    "DAEMON_CREDENTIAL_PATH": str(root / "credential"),
                    "PLATFORM_API_URL": str(client.base_url),
                    "RUNNER_URL": "http://127.0.0.1:1",
                    "RUNNER_STAGE_DIR": str(root / "staged"),
                    "RINGFOUR_APPS_ENABLED": "true",
                    "RINGFOUR_ENGINE_NATIVE_FACTS": "",
                    "RINGFOUR_LOCAL_DEV_ENABLED": "false",
                    "RINGFOUR_AGENT_MOTION_SOCKET": "",
                    "RINGFOUR_ENGINE_SOCKET": socket_path,
                    "RINGFOUR_APP_NETWORK": network,
                    "RINGFOUR_AGENT_URI": agent_uri,
                    "RINGFOUR_APP_INVENTORY_INTERVAL_SECONDS": "0.3",
                    "DAEMON_BACKOFF_INITIAL_SECONDS": "0.1",
                    "DAEMON_BACKOFF_MAX_SECONDS": "0.5",
                    "DAEMON_RECONCILE_DELAY_SECONDS": "2",
                }

                if configure is not None:
                    cleanup = configure(locals())

                def start_daemon():
                    log = (root / f"daemon-{len(logs)}.log").open("w+")
                    logs.append(log)
                    return subprocess.Popen(
                        [
                            sys.executable,
                            "-m",
                            "uvicorn",
                            "ringfour_daemon.main:create_app",
                            "--factory",
                            "--host",
                            daemon_env.get("AP_TEST_BIND", "127.0.0.1"),
                            "--port",
                            str(daemon_port),
                        ],
                        cwd=ROOT,
                        env=daemon_env,
                        stdout=log,
                        stderr=subprocess.STDOUT,
                    )

                daemon = start_daemon()
                daemon_url = f"http://127.0.0.1:{daemon_port}"
                support.wait_for(
                    lambda: httpx.get(daemon_url + "/v1/identity").status_code == 200,
                    "Daemon HTTP",
                )
                checked(
                    httpx.post(daemon_url + "/v1/enroll", json={"token": enrollment})
                )

                def status():
                    return checked(client.get(f"/v1/robots/{robot}/app-status"))

                support.wait_for(
                    lambda: (
                        status().get("inventory", {}).get("state") == "available"
                        if status().get("inventory")
                        else False
                    ),
                    "real Agent/engine inventory",
                )
                initial = status()
                namespace = initial["deployment_namespace"]

                def post(path, body, key=None):
                    return client.post(
                        path,
                        json=body,
                        headers={"Idempotency-Key": key or str(uuid.uuid4())},
                    )

                path = f"/v1/robots/{robot}/app-installations"
                key = str(uuid.uuid4())
                body = {
                    "revision_id": revision["id"],
                    "share_id": None,
                    "configuration": {"sample_count": 1},
                }
                print(
                    "Installing AP-06 Robot Check; interrupting API after grant and before installed acknowledgement",
                    flush=True,
                )
                fault["armed"] = True
                accepted = checked(post(path, body, key))
                installation = accepted["installation_id"]
                operation = accepted["operation_id"]

                def locally_applied():
                    with connect(db) as c:
                        value = c.execute(
                            "SELECT applied_operation_id FROM app_installations WHERE installation_id=?",
                            (installation,),
                        ).fetchone()
                        return value is not None and value[0] == operation

                support.wait_for(
                    locally_applied,
                    "local application with API unavailable",
                    timeout=120,
                )
                support.terminate(daemon)
                daemon = None
                with connect(db) as c:
                    report = json.loads(
                        c.execute(
                            "SELECT body_json FROM app_observation_outbox WHERE route='observations' AND acknowledged_at IS NULL"
                        ).fetchone()[0]
                    )
                    assert report["operations"][0]["operation_id"] == operation
                api = start_api()
                support.wait_for(
                    lambda: client.get("/health/ready").status_code == 200,
                    "recovered API",
                )
                assert checked(post(path, body, key)) == accepted
                daemon = start_daemon()

                def completed(accepted):
                    def check():
                        value = checked(
                            client.get("/v1/app-operations/" + accepted["operation_id"])
                        )
                        return (
                            value if value["state"] in ("succeeded", "failed") else None
                        )

                    return support.wait_for(
                        check, "device operation terminal result", timeout=90
                    )

                assert completed(accepted)["state"] == "succeeded"
                assert status()["deployment_namespace"] == namespace
                resource = "/v1/app-installations/" + installation
                first = checked(client.get(resource))
                assert (
                    first["candidate"] is None
                    and first["installed"]["applied_operation_id"] == operation
                )
                configured = checked(
                    post(
                        resource + "/configure",
                        {
                            "expected_configuration_revision_id": first["installed"][
                                "configuration_revision_id"
                            ],
                            "configuration": {"sample_count": 2},
                        },
                    )
                )
                assert completed(configured)["state"] == "succeeded"
                configured_binding = checked(client.get(resource))["installed"]
                # New metadata release uses the immutable AP-06 image; its schema intentionally excludes the prior count.
                v2 = json.loads(json.dumps(manifest))
                v2["release"] = "0.2.0"
                v2["configuration"]["schema"]["properties"]["sample_count"][
                    "maximum"
                ] = 1
                final = root / "v2.manifest.json"
                final.write_text(json.dumps(v2))
                second = cli("publish", "--manifest", final)
                update = {
                    "expected_revision_id": configured_binding["revision_id"],
                    "expected_configuration_revision_id": configured_binding[
                        "configuration_revision_id"
                    ],
                    "revision_id": second["id"],
                    "share_id": None,
                    "configuration": {"sample_count": 2},
                }
                assert post(resource + "/update", update).status_code == 400
                update["configuration"] = {"sample_count": 1}
                updating = checked(post(resource + "/update", update))
                assert completed(updating)["state"] == "succeeded"
                v2_binding = checked(client.get(resource))["installed"]
                v3 = json.loads(json.dumps(v2))
                v3["release"] = "0.3.0"
                v3["targets"][0]["native_requirements"] = ["ap07.missing_dependency"]
                final.write_text(json.dumps(v3))
                third = cli("publish", "--manifest", final)
                update.update(
                    expected_revision_id=v2_binding["revision_id"],
                    expected_configuration_revision_id=v2_binding[
                        "configuration_revision_id"
                    ],
                    revision_id=third["id"],
                )
                refused = checked(post(resource + "/update", update))
                assert (
                    completed(refused)["failure"]["code"]
                    == "native_requirement_unavailable"
                )
                failed = checked(client.get(resource))
                assert (
                    failed["installed"] == v2_binding
                    and failed["candidate"]["state"] == "failed"
                )
                rolled = checked(
                    post(
                        resource + "/rollback",
                        {
                            "expected_revision_id": v2_binding["revision_id"],
                            "expected_configuration_revision_id": v2_binding[
                                "configuration_revision_id"
                            ],
                            "revision_id": first["installed"]["revision_id"],
                            "configuration_revision_id": configured_binding[
                                "configuration_revision_id"
                            ],
                        },
                    )
                )
                assert completed(rolled)["state"] == "succeeded"
                other = json.loads(json.dumps(manifest))
                other["app_key"] = "ap07-second"
                final.write_text(json.dumps(other))
                other_revision = cli("publish", "--manifest", final)
                other_install = checked(
                    post(
                        path,
                        {
                            "revision_id": other_revision["id"],
                            "share_id": None,
                            "configuration": {"sample_count": 1},
                        },
                    )
                )
                assert completed(other_install)["state"] == "succeeded"
                assert len(checked(client.get(path))["items"]) == 2
                assert all(
                    "/containers/create" not in request and "/start " not in request
                    for request in requests
                )
                with connect(db) as c:
                    assert c.execute("SELECT count(*) FROM app_runs").fetchone() == (0,)
                    assert c.execute(
                        "SELECT count(*) FROM app_installations WHERE revision_id IS NOT NULL"
                    ).fetchone() == (2,)
                print(
                    json.dumps(
                        {
                            "passed": True,
                            "image": image,
                            "installation_id": installation,
                            "operation_id": operation,
                            "report_id": report["report_id"],
                            "api_and_daemon_recovery": True,
                            "configure_update_rollback": True,
                            "failed_update_keeps_binding": True,
                            "two_apps": True,
                            "zero_create_or_start": True,
                        }
                    ),
                    flush=True,
                )
                if run_acceptance is not None:
                    run_acceptance(locals())
        except BaseException:
            for log in logs:
                log.flush()
                log.seek(0)
                text = log.read()
                if "Traceback" in text or "fail:" in text:
                    print(text[-6000:], file=sys.stderr)
            raise
        finally:
            if cleanup is not None:
                cleanup()
            support.terminate(daemon)
            support.terminate(api)
            if server:
                server.shutdown()
                server.server_close()
            for log in logs:
                log.close()
            subprocess.run(
                ["docker", "rm", "-f", "-v", agent, registry, engine, pg],
                capture_output=True,
                check=False,
            )


if __name__ == "__main__":
    main()
