#!/usr/bin/env python3
"""Real Kestrel/PostgreSQL/Distribution acceptance, with a private pull gateway.

Run using apps/daemon/.venv/bin/python. Creates only isolated test containers;
never reloads either Ringfour stack. JWT signing is Development-only test auth.
The gateway enforces short-lived, repository-scoped Bearer reads against a real
Distribution registry; it is a test issuer, not evidence for a hosted provider.
"""
import base64
import hashlib
import json
import os
import secrets
import shutil
import socket
import subprocess
import tempfile
import threading
import time
import uuid
from datetime import UTC, datetime, timedelta
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

import httpx
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding, rsa
from websockets.sync.client import connect

ROOT = Path(__file__).resolve().parents[2]
REGISTRY = "registry:3@sha256:82f6ff4a7f982960c8aa51617fea3aabc485df6e55cce80d22deb1de36f1bc3c"


def command(args, text=None):
    return subprocess.check_output(args, cwd=ROOT, input=text, text=True).strip()


def port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def stamp(value=None):
    return (value or datetime.now(UTC)).isoformat(timespec="microseconds").replace("+00:00", "Z")


def ready(client):
    for _ in range(225):
        try:
            if client.get("/health/ready").status_code == 200:
                return
        except httpx.HTTPError:
            pass
        time.sleep(.2)
    raise RuntimeError("API readiness timed out")


def stop(process):
    if process is not None and process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


def main():
    suffix = uuid.uuid4().hex[:12]
    pg, registry = "ap05-postgres-" + suffix, "ap05-registry-" + suffix
    process = gateway = worker = None
    with tempfile.TemporaryDirectory(prefix="ringfour-ap05-") as directory:
        temp = Path(directory)
        try:
            command(["dotnet", "build", "apps/api/src/Ringfour.Api/Ringfour.Api.csproj", "--no-restore", "--verbosity", "quiet"])
            for name, image, args in [(pg, "postgres:18", ["-e", "POSTGRES_USER=ap05", "-e", "POSTGRES_PASSWORD=ap05-disposable", "-e", "POSTGRES_DB=ap05", "-p", "127.0.0.1::5432"]),
                                      (registry, REGISTRY, ["-p", "127.0.0.1::5000"])]:
                command(["docker", "run", "--rm", "--detach", "--name", name, *args, image])

            def published(name, container_port):
                return json.loads(command(["docker", "inspect", name]))[0]["NetworkSettings"]["Ports"][container_port][0]["HostPort"]

            upstream = "http://127.0.0.1:" + published(registry, "5000/tcp")
            password = secrets.token_urlsafe(24)
            tokens, scopes = {}, []

            class Gateway(BaseHTTPRequestHandler):
                def log_message(self, *_args):
                    pass

                def reply(self, status, body=b"", headers=None):
                    self.send_response(status)
                    for key, value in (headers or {}).items():
                        self.send_header(key, value)
                    self.send_header("Content-Length", str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)

                def do_GET(self):
                    url = urlsplit(self.path)
                    if url.path == "/token":
                        query = parse_qs(url.query)
                        scope = query.get("scope", [""])[0]
                        expected = "Basic " + base64.b64encode(("ap05-reader:" + password).encode()).decode()
                        if self.headers.get("Authorization") != expected or scope != "repository:apps/check:pull" or query.get("service") != ["ap05-registry"]:
                            self.reply(403)
                            return
                        scopes.append(scope)
                        token = secrets.token_urlsafe(32)
                        tokens[token] = time.time() + 120
                        self.reply(200, json.dumps({"token": token, "expires_in": 120}).encode(), {"Content-Type": "application/json"})
                        return
                    if not url.path.startswith("/v2/apps/check/"):
                        self.reply(404)
                        return
                    bearer = self.headers.get("Authorization", "").removeprefix("Bearer ")
                    if tokens.get(bearer, 0) <= time.time():
                        self.reply(401, headers={"WWW-Authenticate": f'Bearer realm="{origin}/token",service="ap05-registry",scope="repository:apps/check:pull,push"'})
                        return
                    response = httpx.get(upstream + self.path, headers={"Accept": self.headers.get("Accept", "application/vnd.oci.image.manifest.v1+json")}, timeout=10, trust_env=False)
                    self.reply(response.status_code, response.content, {k: v for k, v in response.headers.items() if k in ("content-type", "docker-content-digest")})

            gateway = ThreadingHTTPServer(("127.0.0.1", 0), Gateway)
            origin = "http://127.0.0.1:" + str(gateway.server_port)
            worker = threading.Thread(target=gateway.serve_forever, daemon=True)
            worker.start()
            with httpx.Client(base_url=upstream, timeout=15, trust_env=False) as producer:
                for _ in range(100):
                    try:
                        if producer.get("/v2/").status_code == 200:
                            break
                    except httpx.HTTPError:
                        pass
                    time.sleep(.1)
                config = json.dumps({"os": "linux", "architecture": "amd64", "rootfs": {"type": "layers", "diff_ids": []}, "config": {"Cmd": ["/fixture-only"]}}, separators=(",", ":")).encode()
                config_digest = "sha256:" + hashlib.sha256(config).hexdigest()
                upload = producer.post("/v2/apps/check/blobs/uploads/")
                assert upload.status_code == 202, upload.text
                location = upload.headers["Location"]
                assert producer.put(location + ("&" if "?" in location else "?") + "digest=" + config_digest, content=config).status_code == 201
                image_manifest = json.dumps({"schemaVersion": 2, "mediaType": "application/vnd.oci.image.manifest.v1+json", "config": {"mediaType": "application/vnd.oci.image.config.v1+json", "size": len(config), "digest": config_digest}, "layers": []}, separators=(",", ":")).encode()
                response = producer.put("/v2/apps/check/manifests/0.1.0", content=image_manifest, headers={"Content-Type": "application/vnd.oci.image.manifest.v1+json"})
                assert response.status_code == 201, response.text
                image_digest = "sha256:" + hashlib.sha256(image_manifest).hexdigest()
                assert response.headers["Docker-Content-Digest"] == image_digest
            image = urlsplit(origin).netloc + "/apps/check@" + image_digest
            assert httpx.get(origin + "/v2/apps/check/manifests/" + image_digest).status_code == 401

            key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
            encode = lambda data: base64.urlsafe_b64encode(data).rstrip(b"=")

            def jwt(subject):
                header = encode(json.dumps({"alg": "RS256", "kid": "ringfour-test-key"}).encode())
                body = encode(json.dumps({"iss": "https://ap05.test/", "aud": "https://api.ringfour.eu", "sub": subject, "exp": int(time.time()) + 600}).encode())
                signed = header + b"." + body
                return (signed + b"." + encode(key.sign(signed, padding.PKCS1v15(), hashes.SHA256()))).decode()

            api_port = port()
            env = os.environ | {"ASPNETCORE_ENVIRONMENT": "Development", "ASPNETCORE_URLS": f"http://127.0.0.1:{api_port}",
                "ConnectionStrings__Postgres": f"Host=127.0.0.1;Port={published(pg, '5432/tcp')};Database=ap05;Username=ap05;Password=ap05-disposable",
                "Auth__Test__Issuer": "https://ap05.test/", "Auth__Test__Audience": "https://api.ringfour.eu",
                "Auth__Test__RsaPrivateKeyPem": key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()).decode(),
                "Storage__Backend": "filesystem", "Storage__FilesystemRoot": str(temp / "objects"),
                "Apps__Registries__0__Host": urlsplit(origin).netloc, "Apps__Registries__0__RepositoryPrefix": "apps",
                "Apps__Registries__0__AllowHttp": "true", "Apps__Registries__0__AnonymousPull": "false",
                "Apps__Registries__0__TokenEndpoint": origin + "/token", "Apps__Registries__0__Service": "ap05-registry",
                "Apps__Registries__0__Username": "ap05-reader", "Apps__Registries__0__Password": password}
            with (temp / "api.log").open("w+") as log:
                def start():
                    return subprocess.Popen(["dotnet", str(ROOT / "apps/api/src/Ringfour.Api/bin/Debug/net10.0/Ringfour.Api.dll")], cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT)

                process = start()
                clients = [httpx.Client(base_url=f"http://127.0.0.1:{api_port}", headers={"Authorization": "Bearer " + jwt("auth0|ap05-" + who + suffix)}, timeout=15, trust_env=False) for who in "ABC"]
                try:
                    a, b, c = clients
                    ready(a)

                    def post(client, path, body=None, request_key=None, status=201):
                        response = client.post(path, json=body, headers={"Idempotency-Key": request_key or str(uuid.uuid4())}) if body is not None else client.post(path, headers={"Idempotency-Key": request_key or str(uuid.uuid4())})
                        assert response.status_code == status, (response.status_code, response.text)
                        return response.json()

                    org_a, org_b, org_c = [post(client, "/v1/organizations", {"name": "AP05 " + who})["id"] for client, who in zip(clients, "ABC", strict=True)]
                    env["Apps__Registries__0__PublisherOrganizationId"] = org_a
                    env["Apps__OfficialPublisherOrganizationId"] = org_a
                    stop(process); process = start(); ready(a)
                    user_b = b.get("/v1/me").json()["user"]["id"]
                    manifest = json.loads((ROOT / "packages/contracts/fixtures/apps/v1/manifest.json").read_text())
                    manifest["targets"][0]["image"] = image
                    publication = {"manifest": manifest, "visibility": "unlisted"}
                    publish_key = str(uuid.uuid4())
                    revision = post(a, f"/v1/organizations/{org_a}/app-revisions", publication, publish_key)
                    assert b.get("/v1/app-revisions/" + revision["id"]).status_code == 404
                    share = post(a, f'/v1/app-revisions/{revision["id"]}/shares', {"destination_organization_id": org_b, "expires_at": stamp(datetime.now(UTC) + timedelta(hours=1))})
                    request = {"revision_id": revision["id"], "share_id": share["id"]}
                    post(c, f"/v1/organizations/{org_c}/app-imports", request, status=404)
                    import_key = str(uuid.uuid4())
                    imported = post(b, f"/v1/organizations/{org_b}/app-imports", request, import_key)
                    assert imported["publisher_organization_id"] == org_a and imported["source"]["kind"] == "direct_share"
                    post(a, f'/v1/app-shares/{share["id"]}/revoke', status=200)
                    assert post(b, f"/v1/organizations/{org_b}/app-imports", request, import_key) == imported
                    assert post(b, f"/v1/organizations/{org_b}/app-imports", request) == imported
                    assert a.get(f"/v1/organizations/{org_b}/app-imports").status_code == 404
                    assert c.get(f"/v1/organizations/{org_b}/app-imports").status_code == 404
                    listed = json.loads(json.dumps(publication)); listed["manifest"]["release"] = "0.2.0"; listed["visibility"] = "listed"
                    public = post(a, f"/v1/organizations/{org_a}/app-revisions", listed)
                    assert b.get(f"/v1/store/apps?organization_id={org_b}&q=robot-check").json()["items"][0]["id"] == revision["app_id"]
                    assert [r["id"] for r in c.get(f'/v1/apps/{public["app_id"]}/revisions').json()["items"]] == [public["id"]]
                    robot = post(b, f"/v1/organizations/{org_b}/robots", {"name": "AP05 isolated robot"})["id"]
                    enrollment = post(b, f"/v1/robots/{robot}/enrollment-tokens")["token"]
                    credential = post(b, "/v1/device/enroll", {"token": enrollment}, status=200)
                    machine_headers = {"x-ringfour-credential-id": credential["credential_id"], "x-ringfour-credential-secret": credential["credential_secret"]}
                    installation, operation, configuration = [str(uuid.uuid4()) for _ in range(3)]
                    candidate = json.dumps({"operation_id": operation, "revision_id": revision["id"], "configuration_revision_id": configuration, "image": image, "state": "staging", "requested_at": stamp(), "observed_at": None, "staged_at": None, "failure": None})
                    # Controlled AP-07 preparation fixture in this disposable DB only.
                    sql = f"""INSERT INTO app_installations (id,organization_id,robot_id,app_id,import_id,created_by_user_id,created_at,candidate) VALUES ('{installation}','{org_b}','{robot}','{revision['app_id']}','{imported['id']}','{user_b}',now(),'{candidate}');
INSERT INTO app_operations (id,organization_id,robot_id,installation_id,kind,state,phase,requested_by_user_id,requested_at,deadline_at) VALUES ('{operation}','{org_b}','{robot}','{installation}','install','queued','queued','{user_b}',now(),now()+interval '10 minutes');"""
                    command(["docker", "exec", "-i", pg, "psql", "-U", "ap05", "-v", "ON_ERROR_STOP=1"], sql)
                    with connect(f"ws://127.0.0.1:{api_port}/v1/device/connect", additional_headers=machine_headers) as ws:
                        ws.send(json.dumps({"version": 1, "message_id": str(uuid.uuid4()), "type": "hello", "robot_id": robot, "sequence": 1, "payload": {"daemon_version": "0.1.0", "supported_protocol_versions": [1]}}))
                        session = json.loads(ws.recv())["payload"]["session_id"]
                        machine_headers["x-ringfour-device-session"] = session
                        with httpx.Client(base_url=str(a.base_url), headers=machine_headers, trust_env=False, timeout=15) as device:
                            assert device.post("/v1/device/app-protocol/negotiate", json={"session_id": session, "deployment_namespace": str(uuid.uuid4()), "supported_app_protocol_versions": [1]}).status_code == 200
                            response = device.post("/v1/device/app-registry-grants", json={"operation_id": operation, "revision_id": revision["id"], "image": image})
                            assert response.status_code == 200, response.text
                            assert response.headers["cache-control"] == "no-store"
                            grant = response.json()
                            assert grant["robot_id"] == robot and grant["authorization"]["kind"] == "bearer"
                            bearer = grant["authorization"]["token"]
                            pull_headers = {"Authorization": "Bearer " + bearer, "Accept": "application/vnd.oci.image.manifest.v1+json"}
                            assert httpx.get(origin + "/v2/apps/check/manifests/" + image_digest, headers=pull_headers).status_code == 200
                            assert httpx.get(origin + "/v2/apps/check/manifests/sha256:" + "0" * 64, headers=pull_headers).status_code == 404
                            tokens[bearer] = 0
                            assert httpx.get(origin + "/v2/apps/check/manifests/" + image_digest, headers=pull_headers).status_code == 401
                            refreshed = device.post("/v1/device/app-registry-grants", json={"operation_id": operation, "revision_id": revision["id"], "image": image})
                            assert refreshed.status_code == 200 and refreshed.json()["authorization"]["token"] != bearer
                    assert scopes and all(value == "repository:apps/check:pull" for value in scopes)
                    persisted = command(["docker", "exec", pg, "psql", "-U", "ap05", "-Atc", "SELECT outcome FROM app_idempotency_records"])
                    assert all(token not in persisted for token in tokens)
                    gateway.shutdown(); gateway.server_close(); worker.join(timeout=5); gateway = None
                    stop(process); process = start(); ready(a)
                    assert post(a, f"/v1/organizations/{org_a}/app-revisions", publication, publish_key) == revision
                    assert post(b, f"/v1/organizations/{org_b}/app-imports", request, import_key) == imported
                    failed = json.loads(json.dumps(listed)); failed["manifest"]["release"] = "0.3.0"
                    post(a, f"/v1/organizations/{org_a}/app-revisions", failed, status=503)
                    count = command(["docker", "exec", pg, "psql", "-U", "ap05", "-Atc", "SELECT count(*) FROM app_revisions"])
                    assert count == "2"
                    print(json.dumps({"passed": True, "registry_image": REGISTRY, "image_digest": image_digest, "private_pull": True, "three_principals": True,
                        "destination_bound_share": True, "revocation_preserves_import": True, "failed_verification_not_published": True, "expired_token_refreshed": True,
                        "restart_replay_without_registry": True, "tokens_not_persisted": True}, indent=2))
                finally:
                    for client in clients:
                        client.close()
        except Exception:
            if (temp / "api.log").exists():
                shutil.copyfile(temp / "api.log", "/tmp/ap05-api-process.log")
            raise
        finally:
            stop(process)
            if gateway is not None:
                gateway.shutdown(); gateway.server_close()
            for name in (registry, pg):
                subprocess.run(["docker", "rm", "--force", "--volumes", name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)


if __name__ == "__main__":
    main()
