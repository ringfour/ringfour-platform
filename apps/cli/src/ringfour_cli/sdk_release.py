"""Cache an exact, checksum-pinned SDK release wheel for App builds."""

import hashlib
import os
import re
import tempfile
import zipfile
from pathlib import Path

import httpx

VERSION = re.compile(r"[0-9]+\.[0-9]+\.[0-9]+")


def released_checksum(version: str) -> str:
    if not VERSION.fullmatch(version):
        raise ValueError("Use an exact SDK version such as 1.0.0")
    filename = f"ringfour_sdk-{version}-py3-none-any.whl"
    url = f"https://github.com/ringfour/ringfour-sdk/releases/download/v{version}/SHA256SUMS"
    with httpx.Client(timeout=30, follow_redirects=True) as client:
        response = client.get(url)
        response.raise_for_status()
    matches = [line.split() for line in response.text.splitlines() if line.split()[-1:] == [filename]]
    if len(matches) != 1 or len(matches[0]) != 2 or not re.fullmatch(r"[a-f0-9]{64}", matches[0][0]):
        raise ValueError("SDK release has no unique checksummed wheel")
    return matches[0][0]


def cache_dir() -> Path:
    root = Path(os.environ.get("XDG_CACHE_HOME", Path.home() / ".cache"))
    return root / "ringfour" / "sdk-wheels"


def verify_protocol(wheel: Path, version: str) -> None:
    try:
        with zipfile.ZipFile(wheel) as archive:
            source = archive.read("ringfour/version.py").decode("utf-8")
    except (OSError, ValueError, KeyError, UnicodeError, zipfile.BadZipFile) as error:
        raise ValueError("SDK wheel has no readable protocol metadata") from error
    if f'CLIENT_VERSION = "{version}"' not in source or not re.search(
        r"^PROTOCOL_VERSION = 1$", source, re.MULTILINE
    ):
        raise ValueError("SDK wheel is incompatible with Agent protocol 1")


def fetch(version: str, sha256: str) -> Path:
    if not VERSION.fullmatch(version) or not re.fullmatch(r"[a-f0-9]{64}", sha256):
        raise ValueError("Pin an exact SDK version and SHA-256")
    filename = f"ringfour_sdk-{version}-py3-none-any.whl"
    cache = cache_dir()
    destination = cache / filename
    if (
        destination.exists()
        and hashlib.sha256(destination.read_bytes()).hexdigest() == sha256
    ):
        verify_protocol(destination, version)
        return destination
    base = f"https://github.com/ringfour/ringfour-sdk/releases/download/v{version}"
    with httpx.Client(timeout=30, follow_redirects=True) as client:
        sums = client.get(f"{base}/SHA256SUMS")
        sums.raise_for_status()
        listed = [
            line.split()
            for line in sums.text.splitlines()
            if line.split()[-1:] == [filename]
        ]
        if len(listed) != 1 or listed[0] != [sha256, filename]:
            raise ValueError("SDK release checksum differs from App pin")
        cache.mkdir(parents=True, exist_ok=True)
        descriptor, staged = tempfile.mkstemp(dir=cache, prefix=".sdk-")
        try:
            with os.fdopen(descriptor, "wb") as output:
                with client.stream("GET", f"{base}/{filename}") as response:
                    response.raise_for_status()
                    for chunk in response.iter_bytes():
                        output.write(chunk)
                output.flush()
                os.fsync(output.fileno())
            if hashlib.sha256(Path(staged).read_bytes()).hexdigest() != sha256:
                raise ValueError("SDK wheel checksum mismatch")
            verify_protocol(Path(staged), version)
            os.replace(staged, destination)
        finally:
            Path(staged).unlink(missing_ok=True)
    return destination
