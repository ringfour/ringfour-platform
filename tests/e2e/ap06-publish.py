#!/usr/bin/env python3
"""Real CLI -> Docker build/push -> Kestrel/PG acceptance; no Ringfour stack ops."""

import base64
import importlib.util
import json
import os
import subprocess
import tempfile
import time
import uuid
from pathlib import Path

import httpx
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding

ROOT = Path(__file__).resolve().parents[2]
DIND = "docker:29-dind@sha256:4bbc7223f81db790097c6dea42fb93e05b8fead4f0431fbe6ccaba6e8473201c"


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT / "tests/e2e" / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main():
    support = load("ap05_acceptance", "ap05-catalog.py")
    signing = load("ap03_signing", "ap03-inventory.py")
    pg, registry, engine, reader = [
        "ap06-" + kind + "-" + uuid.uuid4().hex[:12]
        for kind in ("postgres", "registry", "engine", "reader")
    ]
    process = None
    with tempfile.TemporaryDirectory(prefix="ringfour-ap06-") as directory:
        temporary = Path(directory)
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
            support.command(
                [
                    "docker",
                    "run",
                    "--rm",
                    "--detach",
                    "--name",
                    pg,
                    "-e",
                    "POSTGRES_USER=ap06",
                    "-e",
                    "POSTGRES_PASSWORD=ap06-disposable",
                    "-e",
                    "POSTGRES_DB=ap06",
                    "-p",
                    "127.0.0.1::5432",
                    "postgres:18",
                ]
            )
            # Disposable nested engines have no host socket or application volumes.
            # A shared test network namespace makes loopback identical for the
            # CLI host gateway, publisher and fresh reader on Docker Desktop.
            registry_port = support.port()
            support.command(
                [
                    "docker",
                    "run",
                    "--rm",
                    "--detach",
                    "--privileged",
                    "--name",
                    engine,
                    "-e",
                    "DOCKER_TLS_CERTDIR=",
                    "-p",
                    "127.0.0.1::2375",
                    "-p",
                    "127.0.0.1::2376",
                    "-p",
                    f"127.0.0.1:{registry_port}:{registry_port}",
                    DIND,
                    "--storage-driver=vfs",
                    "--iptables=false",
                ]
            )

            def published(name, port):
                return json.loads(support.command(["docker", "inspect", name]))[0][
                    "NetworkSettings"
                ]["Ports"][port][0]["HostPort"]

            publisher_host = "tcp://127.0.0.1:" + published(engine, "2375/tcp")
            reader_host = "tcp://127.0.0.1:" + published(engine, "2376/tcp")
            for _ in range(150):
                result = subprocess.run(
                    [
                        "docker",
                        "--host",
                        publisher_host,
                        "version",
                        "--format",
                        "{{.Server.Version}}",
                    ],
                    capture_output=True,
                    check=False,
                )
                if result.returncode == 0:
                    break
                time.sleep(0.2)
            assert result.returncode == 0, "Isolated publisher engine did not start"
            support.command(
                [
                    "docker",
                    "run",
                    "--rm",
                    "--detach",
                    "--name",
                    registry,
                    "--network",
                    "container:" + engine,
                    "-e",
                    f"REGISTRY_HTTP_ADDR=0.0.0.0:{registry_port}",
                    support.REGISTRY,
                ]
            )
            host = f"127.0.0.1:{registry_port}"
            token, pem = signing.token_and_key()
            # Fresh vfs engines can spend minutes unpacking base layers. Keep this
            # ephemeral Development-only signing identity valid for the harness.
            header, payload, _ = token.split(".")
            claims = json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))
            claims["exp"] = int(time.time()) + 3600
            payload = base64.urlsafe_b64encode(json.dumps(claims).encode()).rstrip(b"=").decode()
            signed = (header + "." + payload).encode()
            key = serialization.load_pem_private_key(pem.encode(), password=None)
            token = signed.decode() + "." + base64.urlsafe_b64encode(key.sign(signed, padding.PKCS1v15(), hashes.SHA256())).rstrip(b"=").decode()
            api_port = support.port()
            environment = os.environ | {
                "ASPNETCORE_ENVIRONMENT": "Development",
                "ASPNETCORE_URLS": f"http://127.0.0.1:{api_port}",
                "ConnectionStrings__Postgres": f"Host=127.0.0.1;Port={published(pg, '5432/tcp')};Database=ap06;Username=ap06;Password=ap06-disposable",
                "Auth__Test__Issuer": "https://ap03.test/",
                "Auth__Test__Audience": "https://api.ringfour.eu",
                "Auth__Test__RsaPrivateKeyPem": pem,
                "Storage__Backend": "filesystem",
                "Storage__FilesystemRoot": str(temporary / "objects"),
                "Apps__Registries__0__Host": host,
                "Apps__Registries__0__RepositoryPrefix": "apps",
                "Apps__Registries__0__AllowHttp": "true",
                "Apps__Registries__0__AnonymousPull": "true",
            }
            with (temporary / "api.log").open("w+") as log:

                def start():
                    return subprocess.Popen(
                        [
                            "dotnet",
                            str(
                                ROOT
                                / "apps/api/src/Ringfour.Api/bin/Debug/net10.0/Ringfour.Api.dll"
                            ),
                        ],
                        cwd=ROOT,
                        env=environment,
                        stdout=log,
                        stderr=subprocess.STDOUT,
                    )

                process = start()
                with httpx.Client(
                    base_url=f"http://127.0.0.1:{api_port}",
                    headers={"Authorization": "Bearer " + token},
                    timeout=15,
                    trust_env=False,
                ) as client:
                    support.ready(client)
                    response = client.post(
                        "/v1/organizations", json={"name": "AP06 CLI publisher"}
                    )
                    response.raise_for_status()
                    organization = response.json()["id"]
                    config = temporary / "config.json"
                    config.write_text(
                        json.dumps(
                            {
                                "api_url": str(client.base_url),
                                "auth0_domain": "ap06.example.invalid",
                                "auth0_client_id": "ap06-test",
                                "selected_org": organization,
                            }
                        )
                    )
                    cli_env = os.environ | {
                        "RINGFOUR_CONFIG": str(config),
                        "RINGFOUR_API_URL": str(client.base_url),
                        "RINGFOUR_AUTH0_DOMAIN": "ap06.example.invalid",
                        "RINGFOUR_AUTH0_CLIENT_ID": "ap06-test",
                        "RINGFOUR_AUTH0_AUDIENCE": "https://api.ringfour.eu",
                        "DOCKER_HOST": publisher_host,
                        "PYTHON_KEYRING_BACKEND": "env_keyring.EnvironmentKeyring",
                        "RINGFOUR_TEST_TOKEN_JSON": json.dumps(
                            {"access_token": token, "expires_at": time.time() + 3600}
                        ),
                        "PYTHONPATH": str(ROOT / "apps/cli/tests")
                        + os.pathsep
                        + str(ROOT / "apps/cli/src"),
                    }
                    project = temporary / "robot-check"

                    def cli(*args, success=True):
                        result = subprocess.run(
                            [
                                str(ROOT / "apps/cli/.venv/bin/ringfour-cli"),
                                "app",
                                *map(str, args),
                                "--json",
                            ],
                            env=cli_env,
                            cwd=temporary,
                            capture_output=True,
                            text=True,
                            timeout=300,
                            check=False,
                        )
                        assert (result.returncode == 0) == success, (
                            result.returncode,
                            result.stdout,
                            result.stderr,
                        )
                        return json.loads(result.stdout)

                    print(
                        "Initializing pinned template and building real App image",
                        flush=True,
                    )
                    cli(
                        "init",
                        project,
                        "--app-key",
                        "robot-check",
                        "--repository",
                        host + "/apps/robot-check",
                    )
                    cli("validate", project)
                    (project / ".env").write_text("AP06_SENTINEL=never-in-image")
                    (project / "ringfour.context.json").write_text(
                        '{"private_selection":"never-in-image"}'
                    )
                    built = cli("build", project)
                    image = built["targets"][0]
                    inspection = json.loads(
                        support.command(
                            ["docker", "--host", publisher_host, "image", "inspect", image["image_id"]]
                        )
                    )[0]
                    assert inspection["Config"]["User"] == "10001:10001" and inspection[
                        "Config"
                    ]["Entrypoint"] == ["python", "/app/main.py"]
                    assert not any(
                        "TOKEN=" in value for value in inspection["Config"]["Env"]
                    )
                    support.command(
                        [
                            "docker",
                            "--host",
                            publisher_host,
                            "run",
                            "--rm",
                            "--network",
                            "none",
                            "--entrypoint",
                            "python",
                            image["image_id"],
                            "-c",
                            "from pathlib import Path; from ringfour import Robot; from ringfour_runtime import logbook; assert not Path('/app/.env').exists(); assert not Path('/app/ringfour.context.json').exists()",
                        ]
                    )
                    print(
                        "Pushing with API unavailable; retrying the frozen request after API recovery",
                        flush=True,
                    )
                    cli_env["DOCKER_HOST"] = publisher_host
                    support.stop(process)
                    process = None
                    failure = cli("publish", project, success=False)
                    assert (
                        "error" in failure
                        and (project / ".ringfour/releases/0.1.0.json").exists()
                    ), failure
                    first_snapshot = (
                        project / ".ringfour/releases/0.1.0.json"
                    ).read_bytes()
                    process = start()
                    support.ready(client)
                    first = cli("publish", project)
                    assert first["publisher_organization_id"] == organization
                    assert cli("publish", project) == first
                    cli("validate", "--manifest", project / "app.manifest.json")
                    published_image = first["manifest"]["targets"][0]["image"]
                    print(
                        "Pulling the published digest into a clean image reference",
                        flush=True,
                    )
                    support.command(
                        [
                            "docker",
                            "run",
                            "--rm",
                            "--detach",
                            "--privileged",
                            "--name",
                            reader,
                            "--network",
                            "container:" + engine,
                            "-e",
                            "DOCKER_TLS_CERTDIR=",
                            "--entrypoint",
                            "dockerd",
                            DIND,
                            "--host=tcp://0.0.0.0:2376",
                            "--tls=false",
                            "--storage-driver=vfs",
                            "--iptables=false",
                            "--bridge=none",
                        ]
                    )
                    for _ in range(150):
                        result = subprocess.run(
                            [
                                "docker",
                                "--host",
                                reader_host,
                                "version",
                                "--format",
                                "{{.Server.Version}}",
                            ],
                            capture_output=True,
                            check=False,
                        )
                        if result.returncode == 0:
                            break
                        time.sleep(0.2)
                    assert result.returncode == 0, (
                        "Isolated reader engine did not start"
                    )
                    assert (
                        support.command(
                            ["docker", "--host", reader_host, "image", "ls", "--quiet"]
                        )
                        == ""
                    )
                    support.command(
                        ["docker", "--host", reader_host, "pull", published_image]
                    )
                    assert (
                        json.loads(
                            support.command(
                                [
                                    "docker",
                                    "--host",
                                    reader_host,
                                    "image",
                                    "inspect",
                                    published_image,
                                ]
                            )
                        )[0]["Id"]
                        == image["image_id"]
                    )
                    assert (
                        client.get(
                            f"/v1/store/apps?organization_id={organization}&q=robot-check"
                        ).json()["items"][0]["id"]
                        == first["app_id"]
                    )
                    print(
                        "Publishing changed v2 while retaining v1 snapshot and API metadata",
                        flush=True,
                    )
                    document = json.loads((project / "app.json").read_text())
                    document["release"] = "0.2.0"
                    (project / "app.json").write_text(json.dumps(document))
                    (project / "main.py").write_text(
                        "# AP06 changed revision\n" + (project / "main.py").read_text()
                    )
                    cli("build", project)
                    second = cli("publish", project)
                    assert (
                        second["id"] != first["id"]
                        and second["manifest"]["targets"][0]["image"] != published_image
                    )
                    assert (
                        project / ".ringfour/releases/0.1.0.json"
                    ).read_bytes() == first_snapshot
                    assert (
                        client.get("/v1/app-revisions/" + first["id"]).json() == first
                    )
                    support.command(["docker", "stop", registry])
                    assert cli("publish", project) == second
                    print(
                        json.dumps(
                            {
                                "passed": True,
                                "template_init": True,
                                "clean_build": True,
                                "real_push_and_pull": True,
                                "api_failure_retry": True,
                                "registry_outage_replay": True,
                                "v1_preserved": True,
                                "v2_distinct": True,
                                "publisher": organization,
                                "first_image": published_image,
                                "second_image": second["manifest"]["targets"][0][
                                    "image"
                                ],
                            },
                            indent=2,
                        )
                    )
        finally:
            support.stop(process)
            for name in (reader, registry, engine, pg):
                subprocess.run(
                    ["docker", "rm", "--force", "--volumes", name],
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    check=False,
                )


if __name__ == "__main__":
    main()
