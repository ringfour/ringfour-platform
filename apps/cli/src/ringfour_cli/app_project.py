"""AP-06 local template, clean build context and immutable publication snapshots."""

import copy
import hashlib
import json
import os
import re
import shutil
import subprocess
import tarfile
import tempfile
from importlib.resources import files
from pathlib import Path

from .app_manifest import (
    canonical,
    fingerprint,
    read_json,
    validate_authoring,
    validate_manifest,
)

MEDIA_TYPES = {
    "application/vnd.oci.image.manifest.v1+json",
    "application/vnd.docker.distribution.manifest.v2+json",
}


class DockerError(ValueError):
    pass


def data_home():
    return (
        Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share")) / "ringfour"
    )


def hash_file(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def atomic_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.is_symlink() or path.parent.is_symlink():
        raise ValueError("App state cannot use symlinks")
    descriptor, temporary = tempfile.mkstemp(dir=path.parent, prefix=".app-")
    try:
        with os.fdopen(descriptor, "w") as stream:
            stream.write(canonical(value) + "\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        Path(temporary).unlink(missing_ok=True)


def init_project(destination, app_key, repository, archive=None):
    if destination.exists() or destination.is_symlink():
        raise ValueError("Destination must not already exist")
    pointer = json.loads(
        files("ringfour_cli").joinpath("data/robot-check-template.json").read_text()
    )
    archive = (
        archive or data_home() / "app-templates/robot-check" / pointer["archive_name"]
    )
    if not archive.is_file():
        raise ValueError(
            "Transfer the pinned Robot Check template archive into the local template cache or use --template-archive"
        )
    if (
        archive.stat().st_size > 10 * 1024 * 1024
        or hash_file(archive) != pointer["sha256"]
    ):
        raise ValueError("Template checksum mismatch")
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(
        dir=destination.parent, prefix=".app-init-"
    ) as temporary:
        stage = Path(temporary) / "project"
        stage.mkdir()
        with tarfile.open(archive, "r:gz") as source:
            members = source.getmembers()
            if (
                len(members) > 256
                or sum(member.size for member in members) > 32 * 1024 * 1024
            ):
                raise ValueError("Template exceeds extraction limits")
            for member in members:
                parts = Path(member.name).parts
                if (
                    not parts
                    or parts[0] != "robot-check"
                    or ".." in parts
                    or member.issym()
                    or member.islnk()
                ):
                    raise ValueError("Unsafe template archive")
                target = stage.joinpath(*parts[1:])
                if member.isdir():
                    target.mkdir(parents=True, exist_ok=True)
                elif member.isfile():
                    target.parent.mkdir(parents=True, exist_ok=True)
                    with (
                        source.extractfile(member) as stream,
                        target.open("wb") as output,
                    ):
                        shutil.copyfileobj(stream, output)
                else:
                    raise ValueError("Unsupported template entry")
        document = read_json(stage / "app.json")
        document["app_key"] = app_key
        for target in document["targets"]:
            target["repository"] = repository
        validate_authoring(document)
        atomic_json(stage / "app.json", document)
        atomic_json(stage / "template-origin.json", pointer)
        if destination.exists():
            raise ValueError("Destination already exists")
        stage.rename(destination)
    return {
        "path": str(destination),
        "app_key": app_key,
        "template_version": pointer["version"],
        "template_sha256": pointer["sha256"],
    }


def regular_file(root, name):
    path = root / name
    if (
        any(part.is_symlink() for part in [path, *path.parents] if part != root.parent)
        or not path.is_file()
        or not path.resolve().is_relative_to(root.resolve())
    ):
        raise ValueError(f"Build input must be a regular file without symlinks: {name}")
    return path


def source_snapshot(root, document):
    names = sorted({document["build"]["dockerfile"], *document["build"]["files"]})
    inputs = {name: hash_file(regular_file(root, name)) for name in names}
    return fingerprint({"authoring": document, "inputs": inputs}), names


def docker(*arguments):
    try:
        result = subprocess.run(
            ["docker", *arguments], capture_output=True, check=False, timeout=900
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        raise DockerError("Docker is unavailable or the operation timed out") from error
    if result.returncode:
        raise DockerError(
            f"Docker {' '.join(arguments[:2])} failed; check engine and registry login"
        )
    if len(result.stdout) > 4 * 1024 * 1024:
        raise DockerError("Docker returned an oversized inspection result")
    return result.stdout


def verify_bundle(bundle, document):
    sums = bundle / "SHA256SUMS"
    if hash_file(sums) != document["wheel_bundle"]["sha256"]:
        raise ValueError("Wheel bundle checksum manifest mismatch")
    paths = {}
    for line in sums.read_text().splitlines():
        expected, relative = line.split("  ", 1)
        path = regular_file(bundle, relative)
        if hash_file(path) != expected:
            raise ValueError("Wheel checksum mismatch")
        paths[relative] = expected
    release = read_json(bundle / "release.json")
    if release["bundle_version"] != document["wheel_bundle"]["version"]:
        raise ValueError("Wheel bundle version mismatch")
    if document.get("runtime_settings"):
        runtime = next(
            (
                package
                for package in release.get("packages", [])
                if package.get("name") == "ringfour-platform-runtime"
            ),
            None,
        )
        if runtime is None or _runtime_version(runtime.get("version")) < (0, 1, 1, 0, 0):
            raise ValueError(
                "This App declares live settings but its wheel bundle does not include "
                "ringfour_runtime.settings; use ringfour-platform-runtime 0.1.1.dev0 or newer"
            )
    return paths


def _runtime_version(value):
    text = value if isinstance(value, str) else ""
    match = re.fullmatch(r"(\d+)\.(\d+)\.(\d+)(?:\.dev(\d+))?", text)
    if match is None:
        return (0, 0, 0, 0, 0)
    major, minor, patch, dev = match.groups()
    return (int(major), int(minor), int(patch), 0 if dev is not None else 1, int(dev or 0))


def build_project(root, bundle=None):
    document = validate_authoring(read_json(root / "app.json"))
    source_hash, names = source_snapshot(root, document)
    state = root / ".ringfour"
    frozen = state / "releases" / (document["release"] + ".json")
    if frozen.exists():
        raise ValueError(
            "This App release is immutable. Retry its frozen publish request, or increment `release` in app.json before changing the name or other manifest/source inputs. A metadata-only release can reuse the same OCI image digest."
        )
    bundle = bundle or Path(
        os.environ.get(
            "RINGFOUR_PYTHON_ARTIFACTS",
            data_home() / "python-artifacts" / document["wheel_bundle"]["version"],
        )
    )
    paths = verify_bundle(bundle, document)
    targets = []
    for target in document["targets"]:
        architecture = target["architecture"]
        tag = "ringfour-app-build:" + source_hash + "-" + architecture
        with tempfile.TemporaryDirectory(prefix="ringfour-app-build-") as temporary:
            context = Path(temporary) / "context"
            context.mkdir()
            for name in names:
                destination = context / name
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(root / name, destination)
            copied_hash, _ = source_snapshot(context, document)
            if copied_hash != source_hash:
                raise ValueError("Source inputs changed while staging the build")
            for relative in paths:
                if relative.startswith("linux-" + architecture + "/"):
                    destination = context / "python-wheels" / relative
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(bundle / relative, destination)
                    if hash_file(destination) != paths[relative]:
                        raise ValueError("Wheel inputs changed while staging the build")
            docker(
                "buildx",
                "build",
                "--platform",
                target["os"] + "/" + architecture,
                "--provenance=false",
                "--load",
                "--tag",
                tag,
                "--file",
                str(context / document["build"]["dockerfile"]),
                str(context),
            )
        image = json.loads(docker("image", "inspect", tag, "--format", "{{json .}}"))
        if (image["Os"], image["Architecture"]) != (target["os"], architecture):
            raise DockerError("Built image platform mismatch")
        targets.append({**target, "local_image": tag, "image_id": image["Id"]})
    result = {
        "format_version": 1,
        "release": document["release"],
        "source_fingerprint": source_hash,
        "targets": targets,
    }
    atomic_json(state / "build.json", result)
    return result


def inspect_remote(reference, target):
    descriptor = json.loads(
        docker(
            "buildx",
            "imagetools",
            "inspect",
            reference,
            "--format",
            "{{json .Manifest}}",
        )
    )
    if descriptor.get("mediaType") not in MEDIA_TYPES:
        raise ValueError(
            "A concrete image is required; indexes and mutable final tags are not release targets"
        )
    digest = descriptor["digest"]
    repository = (
        reference.split("@")[0] if "@" in reference else reference.rsplit(":", 1)[0]
    )
    pinned = repository + "@" + digest
    if "@" in reference and reference != pinned:
        raise ValueError("Remote image digest mismatch")
    raw = docker("buildx", "imagetools", "inspect", pinned, "--raw")
    # Buildx may append one presentation newline to the original manifest bytes.
    candidates = [raw, raw[:-1]] if raw.endswith(b"\n") else [raw]
    if not any(
        "sha256:" + hashlib.sha256(value).hexdigest() == digest for value in candidates
    ):
        raise ValueError("Remote manifest bytes do not match their digest")
    image = json.loads(
        docker("buildx", "imagetools", "inspect", pinned, "--format", "{{json .Image}}")
    )
    if (image.get("os"), image.get("architecture")) != (
        target["os"],
        target["architecture"],
    ):
        raise ValueError("Remote image platform mismatch")
    return pinned


def prepare_publication(root):
    document = validate_authoring(read_json(root / "app.json"))
    source_hash, _ = source_snapshot(root, document)
    path = root / ".ringfour/releases" / (document["release"] + ".json")
    if path.exists():
        frozen = read_json(path)
        if frozen["source_fingerprint"] != source_hash:
            raise ValueError(
                "Frozen App release content changed. Increment `release` in app.json before changing the name or other manifest/source inputs; an unchanged OCI image digest may be reused by the new release."
            )
        manifest = validate_manifest(frozen["manifest"])
        # Retry the exact previously verified request; AP-05 replays committed
        # results before consulting a registry that may now be unavailable.
    else:
        built = read_json(root / ".ringfour/build.json")
        if built["source_fingerprint"] != source_hash:
            raise ValueError("Build inputs changed; run app build again")
        manifest = copy.deepcopy(document)
        manifest.pop("build")
        manifest.pop("wheel_bundle")
        for target, image in zip(manifest["targets"], built["targets"], strict=True):
            inspected = json.loads(
                docker(
                    "image", "inspect", image["local_image"], "--format", "{{json .}}"
                )
            )
            if inspected["Id"] != image["image_id"]:
                raise ValueError(
                    "Local build tag was replaced; rebuild before publication"
                )
            tag = (
                target["repository"]
                + ":"
                + document["release"]
                + "-"
                + target["architecture"]
            )
            docker("image", "tag", image["image_id"], tag)
            docker(
                "image",
                "push",
                "--platform",
                target["os"] + "/" + target["architecture"],
                "--quiet",
                tag,
            )
            target["image"] = inspect_remote(tag, target)
            target.pop("repository")
        validate_manifest(manifest)
        atomic_json(path, {"source_fingerprint": source_hash, "manifest": manifest})
    atomic_json(root / "app.manifest.json", manifest)
    return manifest


def verify_prebuilt(path):
    manifest = validate_manifest(read_json(path))
    for target in manifest["targets"]:
        inspect_remote(target["image"], target)
    return manifest
