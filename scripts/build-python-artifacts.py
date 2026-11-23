#!/usr/bin/env python3
"""Build the pinned local Python bundle without using working-copy sources."""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import shutil
import subprocess
import tarfile
import tempfile
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def check(path: Path, expected: str) -> None:
    if digest(path) != expected:
        raise ValueError(f"Checksum mismatch: {path.name}")


def build(output: Path, source_root: Path) -> None:
    release_path = ROOT / "packages/python-artifacts/release.json"
    release = json.loads(release_path.read_text())
    if output.exists():
        raise ValueError(f"Output already exists; retain distributed bundles: {output}")
    uv_version = subprocess.check_output(["uv", "--version"], text=True).split()[1]
    if uv_version != release["uv_version"]:
        raise ValueError(f"Build requires uv {release['uv_version']}; found {uv_version}")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="ringfour-python-build-") as work_text:
        work = Path(work_text)
        built = work / "built"
        for package in release["packages"]:
            archive = subprocess.check_output([
                "git", "-C", str(source_root / package["repository"]),
                "archive", package["commit"], *([package["path"]] if package["path"] else []),
            ])
            extracted = work / package["name"]
            extracted.mkdir()
            with tarfile.open(fileobj=io.BytesIO(archive)) as source:
                source.extractall(extracted, filter="data")
            source = extracted / package["path"]
            subprocess.run([
                "uv", "build", "--python", release["build_python"], "--wheel", "--build-constraints",
                str(source / "build-constraints.txt"), "--out-dir", str(built), str(source),
            ], env=dict(os.environ, SOURCE_DATE_EPOCH=str(package["source_date_epoch"])), check=True)
            check(built / package["filename"], package["sha256"])

        # Assemble beside the destination and publish only after all checks pass.
        with tempfile.TemporaryDirectory(prefix=".ringfour-bundle-", dir=output.parent) as stage_text:
            stage = Path(stage_text)
            shutil.copyfile(release_path, stage / "release.json")
            shutil.copyfile(ROOT / "packages/sdk-image/Dockerfile", stage / "Dockerfile")
            shutil.copyfile(ROOT / "packages/python-artifacts/README.md", stage / "README.md")
            for architecture, target in release["targets"].items():
                directory = stage / f"linux-{architecture}"
                wheels = directory / "wheels"
                wheels.mkdir(parents=True)
                for dependency in target["dependencies"]:
                    destination = wheels / dependency["filename"]
                    with (urllib.request.urlopen(dependency["url"], timeout=60) as response,
                          destination.open("wb") as stream):
                        shutil.copyfileobj(response, stream)
                    check(destination, dependency["sha256"])
                for package in release["packages"]:
                    shutil.copyfile(built / package["filename"], wheels / package["filename"])

                def requirement(item: dict) -> str:
                    return f"{item['name']}=={item['version']} --hash=sha256:{item['sha256']}\n"

                sdk, runtime = release["packages"]
                (directory / "requirements-sdk.txt").write_text(
                    requirement(sdk) + "".join(requirement(d) for d in target["dependencies"]))
                (directory / "requirements-runtime.txt").write_text(requirement(runtime))
                (directory / "requirements.txt").write_text(
                    "-r requirements-sdk.txt\n-r requirements-runtime.txt\n")
            files = sorted(path for path in stage.rglob("*") if path.is_file())
            (stage / "SHA256SUMS").write_text("".join(
                f"{digest(path)}  {path.relative_to(stage).as_posix()}\n" for path in files))
            stage.rename(output)
    print(json.dumps({"bundle": str(output), "version": release["bundle_version"],
                      "manifest_sha256": digest(output / "SHA256SUMS")}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--source-root", type=Path, default=ROOT.parent)
    args = parser.parse_args()
    try:
        build(args.output.resolve(), args.source_root.resolve())
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"{error}\n")
