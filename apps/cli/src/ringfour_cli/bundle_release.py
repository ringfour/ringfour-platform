"""Install an App's pinned Python wheel bundle from a Platform release."""

import os
import re
import tarfile
import tempfile
from pathlib import Path, PurePosixPath

import httpx

from . import app_project
from .app_manifest import read_json, validate_authoring
from .release_update import RELEASES, TAG

ARCHIVE = "python-artifacts.tar.gz"
MAX_DOWNLOAD = 128 * 1024 * 1024
MAX_EXTRACTED = 256 * 1024 * 1024


def fetch_project(root: Path) -> Path:
    document = validate_authoring(read_json(root / "app.json"))
    version = document["wheel_bundle"]["version"]
    destination = app_project.data_home() / "python-artifacts" / version
    if destination.exists():
        app_project.verify_bundle(destination, document)
        return destination

    destination.parent.mkdir(parents=True, exist_ok=True)
    with httpx.Client(timeout=30, follow_redirects=True) as client:
        response = client.get(RELEASES + "/latest")
        response.raise_for_status()
        tag = response.url.path.rstrip("/").split("/")[-1]
        if not TAG.fullmatch(tag):
            raise ValueError("No complete Platform release was found")
        sums = client.get(f"{RELEASES}/download/{tag}/SHA256SUMS")
        sums.raise_for_status()
        matches = [line.split() for line in sums.text.splitlines() if line.split()[-1:] == [ARCHIVE]]
        if len(matches) != 1 or len(matches[0]) != 2 or not re.fullmatch(r"[a-f0-9]{64}", matches[0][0]):
            raise ValueError("The latest Platform release has no checksummed App wheel bundle")
        expected = matches[0][0]
        with tempfile.TemporaryDirectory(prefix=".bundle-", dir=destination.parent) as temporary:
            stage = Path(temporary)
            archive_path = stage / ARCHIVE
            with client.stream("GET", f"{RELEASES}/download/{tag}/{ARCHIVE}", timeout=60) as archive_response:
                archive_response.raise_for_status()
                total = 0
                with archive_path.open("wb") as output:
                    for chunk in archive_response.iter_bytes():
                        total += len(chunk)
                        if total > MAX_DOWNLOAD:
                            raise ValueError("App wheel bundle exceeds the download limit")
                        output.write(chunk)
            if app_project.hash_file(archive_path) != expected:
                raise ValueError("App wheel bundle release checksum mismatch")
            try:
                with tarfile.open(archive_path, "r:gz") as archive:
                    members = archive.getmembers()
                    total = 0
                    for member in members:
                        parts = PurePosixPath(member.name).parts
                        if not parts or parts[0] != version or ".." in parts or not (member.isfile() or member.isdir()):
                            raise ValueError("App wheel bundle has an unsafe archive member")
                        total += member.size
                        if total > MAX_EXTRACTED:
                            raise ValueError("App wheel bundle exceeds the extraction limit")
                    archive.extractall(stage / "extracted", filter="data")
            except tarfile.TarError as error:
                raise ValueError("App wheel bundle archive is invalid") from error
            extracted = stage / "extracted" / version
            app_project.verify_bundle(extracted, document)
            os.replace(extracted, destination)
    return destination
