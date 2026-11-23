#!/usr/bin/env python3
"""Qualify the real local wheel bundle in isolated, offline consumer containers."""
from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
import tempfile
import uuid
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def run(*args: str, success: bool = True) -> subprocess.CompletedProcess:
    result = subprocess.run(args, text=True, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, check=False)
    if (result.returncode == 0) != success:
        raise RuntimeError(f"Unexpected exit {result.returncode}: {args}\n{result.stdout}")
    return result


def qualify(bundle: Path) -> dict:
    release = json.loads((bundle / "release.json").read_text())
    assert release == json.loads((ROOT / "packages/python-artifacts/release.json").read_text())
    for line in (bundle / "SHA256SUMS").read_text().splitlines():
        expected, relative = line.split("  ", 1)
        assert hashlib.sha256((bundle / relative).read_bytes()).hexdigest() == expected, relative
    engine = run("docker", "info", "--format", "{{.OSType}}/{{.Architecture}}").stdout.strip()
    architecture = engine.split("/")[1]
    architecture = {"x86_64": "amd64", "aarch64": "arm64"}.get(architecture, architecture)
    if not engine.startswith("linux/") or architecture not in release["targets"]:
        raise ValueError(f"Unqualified engine target: {engine}")
    suffix = uuid.uuid4().hex[:10]
    images = [f"ringfour-ap04-sdk:{suffix}", f"ringfour-ap04-runtime:{suffix}"]
    try:
        with tempfile.TemporaryDirectory(prefix="ringfour-ap04-consumer-") as temp:
            root = Path(temp)
            # No sibling checkout, vendor path or credential enters this context.
            context = root / "bundle"
            shutil.copytree(bundle, context)
            for image, optional in zip(images, ("false", "true"), strict=True):
                print(f"Building independent consumer, runtime={optional}", flush=True)
                run("docker", "build", "--no-cache", "--network", "none", "--progress", "plain",
                    "--build-arg", f"INSTALL_RUNTIME={optional}", "-t", image, str(context))
                code = """
import importlib.metadata as metadata
import importlib.util
import sys
from ringfour import Robot, PROTOCOL_VERSION
assert callable(Robot.imu) and PROTOCOL_VERSION == 1
assert metadata.version('ringfour-sdk') == '0.1.0.dev0'
assert not any(name in sys.modules for name in ('rclpy', 'gradio'))
"""
                if optional == "true":
                    code += """
from ringfour_runtime import artifacts, interactions, logbook
assert callable(logbook.info) and callable(interactions.create) and callable(artifacts.publish_file)
assert metadata.version('ringfour-platform-runtime') == '0.1.0.dev0'
"""
                else:
                    code += "assert importlib.util.find_spec('ringfour_runtime') is None\n"
                code += "print(Robot.imu)\n"
                run("docker", "run", "--rm", "--network", "none", image, "python", "-c", code)

            print("Installing runtime alone in an isolated environment", flush=True)
            mount = f"type=bind,src={context},dst=/bundle,readonly"
            directory = f"/bundle/linux-{architecture}"
            setup = f"uv venv /tmp/consumer && uv pip install --python /tmp/consumer/bin/python " \
                f"--offline --no-index --no-cache --find-links {directory}/wheels " \
                f"--require-hashes --only-binary=:all: -r {directory}/requirements-runtime.txt"
            run("docker", "run", "--rm", "--network", "none", "--mount", mount, images[0],
                "sh", "-ec", setup + " && /tmp/consumer/bin/python -c \""
                "from ringfour_runtime import artifacts, interactions, logbook; "
                "import importlib.util; assert importlib.util.find_spec('ringfour') is None\"")

            print("Rejecting altered wheel bytes with the original hash lock", flush=True)
            wheel = context / f"linux-{architecture}/wheels" / release["packages"][0]["filename"]
            with zipfile.ZipFile(wheel, "a") as archive:
                archive.writestr("ap04-integrity-negative.txt", "altered wheel bytes")
            setup = setup.replace("requirements-runtime.txt", "requirements-sdk.txt")
            result = run("docker", "run", "--rm", "--network", "none", "--mount", mount,
                         images[0], "sh", "-ec", setup, success=False)
            assert "hash mismatch" in result.stdout.lower(), result.stdout

            print("Rebuilding pinned artifacts and checking byte reproducibility", flush=True)
            rebuilt = root / "rebuilt"
            run("python3", str(ROOT / "scripts/build-python-artifacts.py"), "--output", str(rebuilt))
            assert (rebuilt / "SHA256SUMS").read_bytes() == (bundle / "SHA256SUMS").read_bytes()
            result = run("python3", str(ROOT / "scripts/build-python-artifacts.py"),
                         "--output", str(rebuilt), success=False)
            assert "Output already exists" in result.stdout

        return {"passed": True, "executed_target": f"linux/{architecture}",
                "bundled_targets": list(release["targets"]), "sdk_only": True,
                "optional_runtime": True, "runtime_without_sdk": True,
                "offline_installation": True, "tampered_wheel_refused": True,
                "reproducible_bundle": True, "existing_output_refused": True}
    finally:
        for image in images:
            subprocess.run(["docker", "image", "rm", image], stdout=subprocess.DEVNULL,
                           stderr=subprocess.DEVNULL, check=False)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(qualify(args.bundle.resolve()), indent=2))
