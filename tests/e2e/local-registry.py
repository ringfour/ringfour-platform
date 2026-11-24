#!/usr/bin/env python3
"""Isolated official registry/Joxit/browser/API acceptance; no live stack operation."""

import importlib.util
import json
import os
import subprocess
import tempfile
import time
import uuid
from pathlib import Path

import httpx

ROOT = Path(__file__).resolve().parents[2]


def command(args, env=None):
    result = subprocess.run(
        args, cwd=ROOT, env=env, capture_output=True, text=True, check=False
    )
    if result.returncode:
        raise RuntimeError(
            "Fixture command failed: " + args[0] + "; " + result.stderr[-1800:]
        )
    return result.stdout.strip()


def wait(check):
    for _ in range(200):
        try:
            result = check()
            if result:
                return result
        except (httpx.HTTPError, ValueError):
            pass
        time.sleep(0.2)
    raise RuntimeError("Fixture readiness timed out")


def main():
    spec = importlib.util.spec_from_file_location(
        "ap03_signing", ROOT / "tests/e2e/ap03-inventory.py"
    )
    signing = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(signing)
    prefix = "local-registry-test-" + uuid.uuid4().hex[:10]
    network, volume = prefix + "-net", prefix + "-data"
    registry, ui, pg, api = [
        prefix + "-" + name for name in ("registry", "ui", "pg", "api")
    ]
    tag = None
    with tempfile.TemporaryDirectory(prefix="ringfour-registry-test-") as directory:
        temp = Path(directory)
        try:
            configuration = json.loads(
                command(
                    [
                        "docker",
                        "compose",
                        "-f",
                        "infra/server/docker-compose.yml",
                        "config",
                        "--format",
                        "json",
                    ],
                    env=os.environ
                    | {
                        "POSTGRES_PASSWORD": "fixture",
                        "AUTH0_DOMAIN": "fixture.invalid",
                        "AUTH0_WEB_CLIENT_ID": "fixture",
                    },
                )
            )
            services = configuration["services"]
            command(
                [
                    "dotnet",
                    "build",
                    "apps/api/src/Ringfour.Api/Ringfour.Api.csproj",
                    "--no-restore",
                    "--verbosity",
                    "quiet",
                ]
            )
            command(["docker", "network", "create", network])
            command(["docker", "volume", "create", volume])
            registry_args = []
            for key, value in services["app-registry"]["environment"].items():
                registry_args += ["-e", key + "=" + str(value)]
            command(
                [
                    "docker",
                    "run",
                    "-d",
                    "--name",
                    registry,
                    "--network",
                    network,
                    "--network-alias",
                    "app-registry",
                    "-p",
                    "127.0.0.1:5000:5000",
                    "-v",
                    volume + ":/var/lib/registry",
                    *registry_args,
                    services["app-registry"]["image"],
                ]
            )

            def published(name, port):
                return json.loads(command(["docker", "inspect", name]))[0][
                    "NetworkSettings"
                ]["Ports"][port][0]["HostPort"]

            host = "127.0.0.1:" + published(registry, "5000/tcp")
            registry_origin = "http://" + host
            wait(lambda: httpx.get(registry_origin + "/v2/").status_code == 200)
            ui_args = []
            for key, value in services["registry-ui"]["environment"].items():
                if key == "PULL_URL":
                    value = host
                ui_args += ["-e", key + "=" + str(value)]
            command(
                [
                    "docker",
                    "run",
                    "-d",
                    "--name",
                    ui,
                    "--network",
                    network,
                    "-p",
                    "127.0.0.1::80",
                    *ui_args,
                    services["registry-ui"]["image"],
                ]
            )
            ui_origin = "http://127.0.0.1:" + published(ui, "80/tcp")
            wait(lambda: httpx.get(ui_origin + "/v2/").status_code == 200)
            tag = host + "/ringfour/acceptance:v1"
            command(["docker", "tag", "alpine:3.20", tag])
            command(
                ["docker", "image", "push", "--platform", "linux/amd64", "--quiet", tag]
            )
            raw = command(
                [
                    "docker",
                    "buildx",
                    "imagetools",
                    "inspect",
                    tag,
                    "--format",
                    "{{json .Manifest}}",
                ]
            )
            digest = json.loads(raw)["digest"]
            image = host + "/ringfour/acceptance@" + digest
            command(["docker", "pull", "--platform", "linux/amd64", image])
            catalog = httpx.get(ui_origin + "/v2/_catalog")
            catalog.raise_for_status()
            assert "ringfour/acceptance" in catalog.json()["repositories"]
            manifest = httpx.get(
                ui_origin + "/v2/ringfour/acceptance/manifests/v1",
                headers={
                    "Accept": "application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json"
                },
            )
            manifest.raise_for_status()
            assert manifest.headers["Docker-Content-Digest"] == digest
            # Actual browser renders the catalog/tag and fetches only its own origin.
            print(
                command(
                    ["node", "tests/e2e/local-registry-browser.mjs"],
                    env=os.environ
                    | {
                        "REGISTRY_UI_TEST_ORIGIN": ui_origin,
                        "REGISTRY_TEST_ORIGIN": registry_origin,
                    },
                ),
                flush=True,
            )
            command(["docker", "restart", registry])
            wait(
                lambda: (
                    httpx.get(
                        ui_origin + "/v2/ringfour/acceptance/tags/list"
                    ).status_code
                    == 200
                )
            )
            assert (
                "v1"
                in httpx.get(ui_origin + "/v2/ringfour/acceptance/tags/list").json()[
                    "tags"
                ]
            )
            # API runs in a separate container: loopback image authority must route over Compose DNS.
            command(
                [
                    "docker",
                    "run",
                    "-d",
                    "--name",
                    pg,
                    "--network",
                    network,
                    "--network-alias",
                    "postgres",
                    "-e",
                    "POSTGRES_USER=acceptance",
                    "-e",
                    "POSTGRES_PASSWORD=fixture",
                    "-e",
                    "POSTGRES_DB=acceptance",
                    "postgres:18",
                ]
            )
            wait(
                lambda: (
                    subprocess.run(
                        [
                            "docker",
                            "exec",
                            pg,
                            "pg_isready",
                            "-U",
                            "acceptance",
                            "-d",
                            "acceptance",
                        ],
                        capture_output=True,
                        check=False,
                    ).returncode
                    == 0
                )
            )
            token, pem = signing.token_and_key()
            settings = {
                "ConnectionStrings": {
                    "Postgres": "Host=postgres;Port=5432;Database=acceptance;Username=acceptance;Password=fixture"
                },
                "Auth": {
                    "Test": {
                        "Issuer": "https://ap03.test/",
                        "Audience": "https://api.ringfour.eu",
                        "RsaPrivateKeyPem": pem,
                    }
                },
                "Storage": {"Backend": "filesystem", "FilesystemRoot": "/tmp/objects"},
                "Apps": {
                    "Registries": [
                        {
                            "Host": host,
                            "RepositoryPrefix": "ringfour",
                            "AllowHttp": True,
                            "AnonymousPull": True,
                            "ApiEndpoint": "http://app-registry:5000",
                        }
                    ]
                },
            }
            (temp / "appsettings.json").write_text(json.dumps(settings))
            binary = ROOT / "apps/api/src/Ringfour.Api/bin/Debug/net10.0"
            command(
                [
                    "docker",
                    "run",
                    "-d",
                    "--name",
                    api,
                    "--network",
                    network,
                    "--workdir",
                    "/acceptance",
                    "-p",
                    "127.0.0.1::8080",
                    "-e",
                    "ASPNETCORE_ENVIRONMENT=Development",
                    "-e",
                    "ASPNETCORE_URLS=http://+:8080",
                    "--mount",
                    f"type=bind,src={temp},dst=/acceptance,readonly",
                    "--mount",
                    f"type=bind,src={binary},dst=/app,readonly",
                    "--entrypoint",
                    "dotnet",
                    "mcr.microsoft.com/dotnet/sdk:10.0",
                    "/app/Ringfour.Api.dll",
                ]
            )
            api_origin = "http://127.0.0.1:" + published(api, "8080/tcp")
            wait(lambda: httpx.get(api_origin + "/health/ready").status_code == 200)
            with httpx.Client(
                base_url=api_origin,
                headers={"Authorization": "Bearer " + token},
                timeout=15,
            ) as client:
                response = client.post(
                    "/v1/organizations", json={"name": "Local registry acceptance"}
                )
                response.raise_for_status()
                org = response.json()["id"]
                document = json.loads(
                    (
                        ROOT / "packages/contracts/fixtures/apps/v1/manifest.json"
                    ).read_text()
                )
                document["targets"][0]["image"] = image
                response = client.post(
                    f"/v1/organizations/{org}/app-revisions",
                    headers={"Idempotency-Key": prefix},
                    json={"manifest": document, "visibility": "listed"},
                )
                response.raise_for_status()
                published_revision = response.json()
                assert published_revision["manifest"]["targets"][0]["image"] == image
            print(
                json.dumps(
                    {
                        "passed": True,
                        "registry_image": services["app-registry"]["image"],
                        "ui_image": services["registry-ui"]["image"],
                        "push_pull_digest": digest,
                        "ui_proxy_catalog_and_manifest": True,
                        "browser_cors_ok": True,
                        "registry_restart_retains_image": True,
                        "containerized_api_verifies_internal_endpoint": True,
                        "image_authority_preserved": True,
                    }
                ),
                flush=True,
            )
        finally:
            subprocess.run(
                ["docker", "rm", "-f", "-v", api, pg, ui, registry],
                capture_output=True,
                check=False,
            )
            subprocess.run(
                ["docker", "volume", "rm", volume], capture_output=True, check=False
            )
            subprocess.run(
                ["docker", "network", "rm", network], capture_output=True, check=False
            )
            if tag:
                subprocess.run(
                    ["docker", "image", "rm", tag], capture_output=True, check=False
                )


if __name__ == "__main__":
    main()
