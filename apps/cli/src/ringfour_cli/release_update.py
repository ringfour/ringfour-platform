"""Verified CLI release checks and user-initiated upgrades."""

import hashlib
import json
import os
import re
import subprocess
import tempfile
import time
import uuid
from pathlib import Path

import httpx

from . import __version__

RELEASES = "https://github.com/ringfour/ringfour-platform/releases"
WHEEL = re.compile(r"ringfour_cli-([0-9][A-Za-z0-9.]+)-py3-none-any\.whl")
TAG = re.compile(r"main-[0-9]+-[0-9]+")


def version_key(value: str) -> tuple[int, int, int, int] | None:
    match = re.fullmatch(r"(\d+)\.(\d+)\.(\d+)(?:\.dev(\d+))?", value)
    if not match:
        return None
    major, minor, patch, dev = match.groups()
    return int(major), int(minor), int(patch), int(dev) if dev else 10**12


def cache_path() -> Path:
    root = Path(os.environ.get("XDG_CACHE_HOME", Path.home() / ".cache"))
    return root / "ringfour" / "cli-release.json"


def cached_notice() -> str | None:
    try:
        data = json.loads(cache_path().read_text())
        if (
            time.time() - data["checked_at"] < 86400
            and version_key(data["version"]) is not None
            and version_key(__version__) is not None
            and version_key(data["version"]) > version_key(__version__)
        ):
            return f"CLI {data['version']} is available. Run ringfour-cli update."
    except (OSError, ValueError, KeyError, TypeError):
        pass
    return None


def check() -> dict:
    with httpx.Client(timeout=10, follow_redirects=True) as client:
        response = client.get(RELEASES + "/latest")
        response.raise_for_status()
        tag = response.url.path.rstrip("/").split("/")[-1]
        if not TAG.fullmatch(tag):
            raise ValueError("No compatible Platform CLI release was found")
        sums = client.get(f"{RELEASES}/download/{tag}/SHA256SUMS")
        sums.raise_for_status()
    matches = [
        (digest, name)
        for digest, name in (
            line.split() for line in sums.text.splitlines() if line.strip()
        )
        if WHEEL.fullmatch(name) and re.fullmatch(r"[a-f0-9]{64}", digest)
    ]
    if len(matches) != 1:
        raise ValueError("Release must contain exactly one checksummed CLI wheel")
    digest, filename = matches[0]
    version = WHEEL.fullmatch(filename).group(1)
    if version_key(version) is None:
        raise ValueError("Release has an invalid CLI version")
    data = {
        "tag": tag,
        "version": version,
        "sha256": digest,
        "filename": filename,
        "checked_at": time.time(),
    }
    path = cache_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    stage = path.with_suffix(".tmp")
    stage.write_text(json.dumps(data) + "\n")
    stage.replace(path)
    return data


def update() -> dict:
    release = check()
    if version_key(release["version"]) <= version_key(__version__):
        return release
    with tempfile.TemporaryDirectory() as directory:
        wheel = Path(directory) / release["filename"]
        with httpx.stream(
            "GET",
            f"{RELEASES}/download/{release['tag']}/{wheel.name}",
            follow_redirects=True,
            timeout=60,
        ) as response:
            response.raise_for_status()
            with wheel.open("wb") as output:
                for chunk in response.iter_bytes():
                    output.write(chunk)
        if hashlib.sha256(wheel.read_bytes()).hexdigest() != release["sha256"]:
            raise ValueError("CLI wheel checksum does not match the release")
        # Keep the existing uv tool and command usable until the new entrypoint
        # has been installed and exercised. A versioned directory also keeps
        # the new entrypoint's interpreter path stable after activation.
        data_home = Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share"))
        stage = data_home / "ringfour" / "cli-releases" / f"{release['version']}-{uuid.uuid4().hex}"
        stage_bin = stage / "bin"
        stage_tools = stage / "tools"
        stage_bin.mkdir(parents=True)
        environment = dict(os.environ, UV_TOOL_DIR=str(stage_tools), UV_TOOL_BIN_DIR=str(stage_bin))
        subprocess.run(
            ["uv", "tool", "install", "--force", "--python", "3.13", str(wheel)],
            check=True,
            env=environment,
        )
        candidate = stage_bin / "ringfour-cli"
        result = subprocess.run([str(candidate), "version"], check=True, capture_output=True, text=True)
        if result.stdout.strip() != release["version"]:
            raise ValueError("Staged CLI version does not match its release")
        bin_dir = Path(subprocess.run(["uv", "tool", "dir", "--bin"], check=True, capture_output=True, text=True).stdout.strip())
        bin_dir.mkdir(parents=True, exist_ok=True)
        active = bin_dir / "ringfour-cli"
        replacement = bin_dir / f".ringfour-cli-{uuid.uuid4().hex}"
        try:
            replacement.symlink_to(candidate)
            replacement.replace(active)
        finally:
            replacement.unlink(missing_ok=True)
    return release
