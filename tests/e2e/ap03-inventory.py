#!/usr/bin/env python3
"""Isolated real-process Agent hello -> Daemon -> PostgreSQL/API acceptance.

Run with apps/daemon/.venv/bin/python tests/e2e/ap03-inventory.py.
Uses the existing Agent image only as a Python dependency environment, mounting
the actual sibling Agent/SDK source read-only. Runs the real Agent protocol
without ROS, so this proves advertised inventory, never live sensor readings.
No Ringfour stack is started/restarted/rebuilt by this harness.
"""

import base64
import json
import os
import socket
import subprocess
import sys
import tempfile
import time
import uuid
from pathlib import Path

import httpx
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding, rsa

ROOT = Path(__file__).resolve().parents[2]


def command(args):
    return subprocess.check_output(args, cwd=ROOT, text=True).strip()


def port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def wait_for(check, description, timeout=45):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            result = check()
            if result:
                return result
        except (httpx.HTTPError, KeyError, ValueError):
            pass
        time.sleep(0.2)
    raise RuntimeError("Timed out: " + description)


def terminate(process):
    if process is not None and process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


def token_and_key():
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    encode = lambda data: base64.urlsafe_b64encode(data).rstrip(b"=")
    header = encode(json.dumps({"alg": "RS256", "typ": "JWT", "kid": "ringfour-test-key"}).encode())
    body = encode(json.dumps({"iss": "https://ap03.test/", "aud": "https://api.ringfour.eu",
                              "sub": "auth0|ap03-" + uuid.uuid4().hex, "exp": int(time.time()) + 600}).encode())
    signed = header + b"." + body
    token = signed + b"." + encode(key.sign(signed, padding.PKCS1v15(), hashes.SHA256()))
    pem = key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()).decode()
    return token.decode(), pem


def main():
    identifier = uuid.uuid4().hex[:12]
    pg = "ap03-test-postgres-" + identifier
    agent = "ap03-test-agent-" + identifier
    api_process = daemon_process = None
    logs = []
    with tempfile.TemporaryDirectory(prefix="ringfour-ap03-") as directory:
        temp = Path(directory)
        try:
            command(["dotnet", "build", "apps/api/src/Ringfour.Api/Ringfour.Api.csproj", "--no-restore"])
            command(["docker", "run", "--rm", "--detach", "--name", pg,
                     "-e", "POSTGRES_USER=ap03", "-e", "POSTGRES_PASSWORD=ap03-disposable",
                     "-e", "POSTGRES_DB=ap03", "-p", "127.0.0.1::5432", "postgres:18"])
            inspect = json.loads(command(["docker", "inspect", pg]))[0]
            pg_port = inspect["NetworkSettings"]["Ports"]["5432/tcp"][0]["HostPort"]
            connection = f"Host=127.0.0.1;Port={pg_port};Database=ap03;Username=ap03;Password=ap03-disposable"

            # Use the exact planning-baseline profile; local profile edits cannot
            # change the expected fixture or make a missing capability pass.
            profile = command(["git", "-C", str(ROOT.parent / "ringfour-virtual/dependencies/base101"),
                               "show", "1f1a4004ac7dfb2257b8ce7a6e40fa4a9a795ece:profiles/base101.yaml"])
            (temp / "profile.yaml").write_text(profile)
            (temp / "agent.py").write_text('''import asyncio, tempfile
from ringfour_agent.bootstrap import build_agent
from ringfour_agent.profile import load_profile
async def main():
    with tempfile.TemporaryDirectory() as directory:
        server, ctx = build_agent(load_profile('/acceptance/profile.yaml'), unix_path=None,
                                 host='0.0.0.0', port=10101, audit_dir=directory, ros=None)
        await server.start()
        try:
            await asyncio.Event().wait()
        finally:
            await server.stop()
            ctx.audit.close()
asyncio.run(main())
''')
            command(["docker", "run", "--detach", "--name", agent,
                     "--mount", f"type=bind,src={temp},dst=/acceptance,readonly",
                     "--mount", f"type=bind,src={ROOT.parent / 'ringfour-agent'},dst=/agent-src,readonly",
                     "--mount", f"type=bind,src={ROOT.parent / 'ringfour-sdk'},dst=/sdk-src,readonly",
                     "-e", "PYTHONPATH=/agent-src/src/ringfour_agent:/sdk-src",
                     "-p", "127.0.0.1::10101", "--entrypoint", "/bin/bash", "ringfour-agent:local", "-c",
                     "source /opt/ringfour/setup.bash; export PYTHONPATH=/agent-src/src/ringfour_agent:/sdk-src:${PYTHONPATH:-}; exec python3 /acceptance/agent.py"])
            agent_port = json.loads(command(["docker", "inspect", agent]))[0]["NetworkSettings"]["Ports"]["10101/tcp"][0]["HostPort"]
            api_port, daemon_port = port(), port()
            jwt, pem = token_and_key()
            api_log = (temp / "api.log").open("w+"); logs.append(api_log)
            env = os.environ | {
                "ASPNETCORE_ENVIRONMENT": "Development", "ConnectionStrings__Postgres": connection,
                "Auth__Test__Issuer": "https://ap03.test/", "Auth__Test__Audience": "https://api.ringfour.eu",
                "Auth__Test__RsaPrivateKeyPem": pem, "Storage__Backend": "filesystem",
                "Storage__FilesystemRoot": str(temp / "objects"),
                "ASPNETCORE_URLS": f"http://127.0.0.1:{api_port}",
            }
            api_process = subprocess.Popen(["dotnet", str(ROOT / "apps/api/src/Ringfour.Api/bin/Debug/net10.0/Ringfour.Api.dll")],
                                           cwd=ROOT, env=env, stdout=api_log, stderr=subprocess.STDOUT)
            with httpx.Client(base_url=f"http://127.0.0.1:{api_port}", headers={"Authorization": "Bearer " + jwt}, timeout=15) as client:
                wait_for(lambda: client.get("/health/ready").status_code == 200, "API readiness")
                response = client.post("/v1/organizations", json={"name": "AP03 isolated acceptance"}); response.raise_for_status()
                org = response.json()["id"]
                response = client.post(f"/v1/organizations/{org}/robots", json={"name": "Agent inventory robot"}); response.raise_for_status()
                robot = response.json()["id"]
                response = client.post(f"/v1/robots/{robot}/enrollment-tokens"); response.raise_for_status()
                enrollment = response.json()["token"]
                engine_os, engine_arch = command(["docker", "info", "--format", "{{.OSType}} {{.Architecture}}"]).split()
                daemon_env = os.environ | {
                    "PYTHONPATH": str(ROOT / "apps/daemon/src"), "DAEMON_DB_PATH": str(temp / "daemon.db"),
                    "DAEMON_CREDENTIAL_PATH": str(temp / "credential"), "PLATFORM_API_URL": str(client.base_url),
                    "RINGFOUR_AGENT_URI": f"ws://127.0.0.1:{agent_port}", "RINGFOUR_ENGINE_OS": engine_os,
                    "RINGFOUR_ENGINE_ARCHITECTURE": engine_arch, "RINGFOUR_APP_INVENTORY_INTERVAL_SECONDS": "0.3",
                    "RINGFOUR_AGENT_INVENTORY_TIMEOUT_SECONDS": "1",
                }
                def start_daemon():
                    log = (temp / ("daemon-" + str(len(logs)) + ".log")).open("w+"); logs.append(log)
                    return subprocess.Popen([sys.executable, "-c",
                        "import uvicorn; from ringfour_daemon.main import create_app; uvicorn.run(create_app(),host='127.0.0.1',port=" + str(daemon_port) + ")"],
                        cwd=ROOT, env=daemon_env, stdout=log, stderr=subprocess.STDOUT)
                daemon_process = start_daemon()
                daemon_url = f"http://127.0.0.1:{daemon_port}"
                wait_for(lambda: httpx.get(daemon_url + "/v1/identity").status_code == 200, "Daemon startup")
                response = httpx.post(daemon_url + "/v1/enroll", json={"token": enrollment}); response.raise_for_status()
                def status(state=None):
                    if state == "available":
                        running = json.loads(command(["docker", "inspect", agent]))[0]["State"]["Running"]
                        if not running:
                            raise RuntimeError("Agent process exited; see its logs")
                    response = client.get(f"/v1/robots/{robot}/app-status"); response.raise_for_status()
                    body = response.json()
                    return body if state is None or body.get("inventory", {}) and body["inventory"].get("state") == state else None
                available = wait_for(lambda: status("available"), "real Agent welcome report")
                current = available["inventory"]["current"]
                assert current["capabilities"] == ["mobility", "cameras", "lidar", "imu", "slam", "teleop", "watches", "places", "audit", "status"]
                assert current["instances"] == {"cameras": ["base"]}
                assert available["support"] == "supported"
                namespace, sequence = available["deployment_namespace"], available["report_sequence"]
                terminate(daemon_process)
                daemon_process = start_daemon()
                restarted = wait_for(lambda: (s if (s := status("available")) and s["report_sequence"] > sequence else None), "Daemon restart with durable sequence")
                assert restarted["deployment_namespace"] == namespace
                command(["docker", "stop", agent])
                unavailable = wait_for(lambda: status("unavailable"), "Agent-unavailable report")
                assert unavailable["inventory"]["last_successful"]["capabilities"] == current["capabilities"]
                assert unavailable["inventory"]["reason"] == "agent_unreachable"
                terminate(daemon_process); daemon_process = None
                offline = wait_for(lambda: (s if (s := status()) and s["connectivity"] == "offline" else None), "offline projection")
                assert offline["inventory"]["state"] == "unavailable"
                print(json.dumps({"result": "passed", "agent": "real Agent protocol without ROS", "capabilities": current["capabilities"],
                                  "instances": current["instances"], "namespace_retained_on_restart": True,
                                  "report_sequence_advanced": True, "unavailable_preserves_last_successful": True,
                                  "offline_projection_retained": True}, indent=2))
        except BaseException:
            for log in logs:
                log.flush(); log.seek(0)
                print(log.read()[-6000:], file=sys.stderr)
            subprocess.run(["docker", "logs", agent], check=False, stdout=sys.stderr, stderr=sys.stderr)
            raise
        finally:
            terminate(daemon_process); terminate(api_process)
            for name in (agent, pg):
                subprocess.run(["docker", "stop", name], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            subprocess.run(["docker", "rm", "-v", agent], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            for log in logs: log.close()


if __name__ == "__main__":
    main()
