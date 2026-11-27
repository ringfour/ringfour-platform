"""Fetch verified SDK or Agent release wheels into the Daemon's local cache."""

import argparse
import hashlib
import os
import re
import tempfile
from pathlib import Path

import httpx


def fetch(package: str, version: str, sha256: str, cache: Path) -> Path:
    if package not in ("sdk", "agent") or not re.fullmatch(
        r"[0-9]+\.[0-9]+\.[0-9]+", version
    ):
        raise ValueError("Choose sdk or agent and an exact version")
    if not re.fullmatch(r"[a-f0-9]{64}", sha256):
        raise ValueError("Pin the released SHA-256")
    filename = f"ringfour_{package}-{version}-py3-none-any.whl"
    destination = cache / filename
    if (
        destination.exists()
        and hashlib.sha256(destination.read_bytes()).hexdigest() == sha256
    ):
        return destination
    base = (
        f"https://github.com/ringfour/ringfour-{package}/releases/download/v{version}"
    )
    with httpx.Client(timeout=30, follow_redirects=True) as client:
        response = client.get(base + "/SHA256SUMS")
        response.raise_for_status()
        matching = [
            line.split()
            for line in response.text.splitlines()
            if line.split()[-1:] == [filename]
        ]
        if matching != [[sha256, filename]]:
            raise ValueError("Release checksum differs from requested artifact")
        cache.mkdir(parents=True, exist_ok=True)
        descriptor, staged = tempfile.mkstemp(dir=cache, prefix=".wheel-")
        try:
            with os.fdopen(descriptor, "wb") as output:
                with client.stream("GET", base + "/" + filename) as wheel:
                    wheel.raise_for_status()
                    for chunk in wheel.iter_bytes():
                        output.write(chunk)
                output.flush()
                os.fsync(output.fileno())
            if hashlib.sha256(Path(staged).read_bytes()).hexdigest() != sha256:
                raise ValueError("Wheel checksum mismatch")
            os.replace(staged, destination)
        finally:
            Path(staged).unlink(missing_ok=True)
    return destination


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", choices=("sdk", "agent"))
    parser.add_argument("version")
    parser.add_argument("sha256")
    parser.add_argument(
        "--cache-dir",
        type=Path,
        default=Path(os.environ.get("XDG_CACHE_HOME", Path.home() / ".cache"))
        / "ringfour"
        / "toolchains",
    )
    arguments = parser.parse_args()
    try:
        print(
            fetch(
                arguments.package,
                arguments.version,
                arguments.sha256,
                arguments.cache_dir,
            )
        )
    except (httpx.HTTPError, OSError, ValueError) as error:
        parser.exit(1, f"Toolchain fetch failed: {error}\n")


if __name__ == "__main__":
    main()
