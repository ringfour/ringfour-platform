"""Parse and deterministically package an immutable mission directory."""
import codecs
import hashlib
import json
import os
import re
import shutil
import stat
import tempfile
import tomllib
import zipfile
from dataclasses import dataclass
from pathlib import Path

from .mission_context import CONTEXT_FILE


@dataclass
class FileBundle:
    archive: object
    archive_size: int
    archive_sha256: str
    revision_digest: str
    entrypoint: str
    lock_sha256: str

    def revision_request(self, organization_id: str, python_requirement: str) -> dict:
        return {"organization_id": organization_id, "archive_sha256": self.archive_sha256,
                "archive_size": self.archive_size, "revision_digest": self.revision_digest,
                "entrypoint": self.entrypoint, "python_requirement": python_requirement, "lock_sha256": self.lock_sha256}

    def close(self):
        self.archive.close()


@dataclass
class MissionBundle:
    bundle: FileBundle
    manifest: dict


def _open_under(root: Path, relative: str):
    """Open a regular file through no-follow directory descriptors."""
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
    current = os.open(root, flags)
    try:
        parts = relative.split("/")
        for part in parts[:-1]:
            following = os.open(part, flags, dir_fd=current)
            os.close(current)
            current = following
        descriptor = os.open(parts[-1], os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=current)
        try:
            if not stat.S_ISREG(os.fstat(descriptor).st_mode):
                raise ValueError("Mission entries must be regular files.")
            return os.fdopen(descriptor, "rb")
        except BaseException:
            os.close(descriptor)
            raise
    finally:
        os.close(current)


def _pack_mission(root: Path, files: dict[str, str], entrypoint: str, progress=None) -> FileBundle:
    """Write and hash a deterministic ZIP64 archive using bounded buffers."""
    output = tempfile.TemporaryFile(mode="w+b")  # noqa: SIM115 - caller owns the archive until upload completes
    digests = []
    total = 0
    next_report = 64 * 1024 * 1024
    next_space_check = 0
    try:
        with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for name, source in sorted(files.items()):
                info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o100644 << 16
                checksum = hashlib.sha256()
                decoder = codecs.getincrementaldecoder("utf-8")("strict") if name == "requirements.lock" else None
                with archive.open(info, "w", force_zip64=True) as target, _open_under(root, source) as stream:
                    before = os.fstat(stream.fileno())
                    length = 0
                    while chunk := stream.read(65536):
                        length += len(chunk)
                        total += len(chunk)
                        if total > 9007199254740991:
                            raise ValueError("Mission exceeds the supported exact integer range.")
                        if total >= next_space_check:
                            if shutil.disk_usage(tempfile.gettempdir()).free < 268435456 + len(chunk):
                                raise ValueError("Not enough temporary disk space to package the mission.")
                            next_space_check = total + 16 * 1024 * 1024
                        checksum.update(chunk)
                        if decoder is not None:
                            decoder.decode(chunk)
                        target.write(chunk)
                        if progress and total >= next_report:
                            progress(total)
                            next_report = total + 64 * 1024 * 1024
                    if decoder is not None:
                        decoder.decode(b"", final=True)
                    after = os.fstat(stream.fileno())
                    if length != before.st_size or (before.st_size, before.st_mtime_ns, before.st_ctime_ns) != (after.st_size, after.st_mtime_ns, after.st_ctime_ns):
                        raise ValueError("Mission source changed during packaging.")
                digests.append([name, checksum.hexdigest()])
        output.flush()
        size = output.tell()
        if size > 9007199254740991:
            raise ValueError("Archive exceeds the supported exact integer range.")
        output.seek(0)
        checksum = hashlib.file_digest(output, "sha256").hexdigest()
        output.seek(0)
        digest = hashlib.sha256(json.dumps(digests, ensure_ascii=False, separators=(",", ":")).encode()).hexdigest()
        lock_sha256 = next(value for name, value in digests if name == "requirements.lock")
        return FileBundle(output, size, checksum, digest, entrypoint, lock_sha256)
    except BaseException:
        output.close()
        raise


def _valid_relative(path: object) -> bool:
    return isinstance(path, str) and 0 < len(path) <= 240 and not path.startswith("/") and all(
        part not in ("", ".", "..") and re.fullmatch(r"[A-Za-z0-9_.-]+", part)
        for part in path.split("/")
    )


def _read_under(root: Path, relative: str, limit: int) -> bytes:
    """Open using directory descriptors, refusing links even if entries change."""
    directory_flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
    current = os.open(root, directory_flags)
    try:
        parts = relative.split("/")
        for part in parts[:-1]:
            following = os.open(part, directory_flags, dir_fd=current)
            os.close(current)
            current = following
        descriptor = os.open(parts[-1], os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=current)
        try:
            if not stat.S_ISREG(os.fstat(descriptor).st_mode):
                raise ValueError("Mission entries must be regular files.")
            with os.fdopen(descriptor, "rb", closefd=False) as source:
                data = source.read(limit + 1)
        finally:
            os.close(descriptor)
        if len(data) > limit:
            raise ValueError("Mission file exceeds its byte limit.")
        return data
    finally:
        os.close(current)


def build_mission_bundle(directory: Path, progress=None) -> MissionBundle:
    if directory.is_symlink() or not directory.is_dir():
        raise ValueError("Mission directory must be a directory, not a symlink.")
    try:
        raw = _read_under(directory, "mission.toml", 65536)
        local = tomllib.loads(raw.decode("utf-8"))
    except (OSError, UnicodeError, tomllib.TOMLDecodeError) as exc:
        raise ValueError("Mission needs a readable UTF-8 mission.toml.") from exc
    required = {"schema_version", "name", "entrypoint", "python_requirement", "lock_file"}
    if not required <= set(local) or set(local) - required - {"robot_profile_ref"}:
        raise ValueError("Mission manifest has missing or unknown fields.")
    mission_name, entrypoint, requirement, lock_path = (local[key] for key in ("name", "entrypoint", "python_requirement", "lock_file"))
    if type(local["schema_version"]) is not int or local["schema_version"] != 1:
        raise ValueError("Mission schema_version must be 1.")
    if not isinstance(mission_name, str) or not mission_name.strip() or len(mission_name) > 100:
        raise ValueError("Mission name must contain 1–100 characters.")
    if not isinstance(requirement, str) or not requirement.strip() or len(requirement) > 128:
        raise ValueError("Mission python_requirement must contain 1–128 characters.")
    if not _valid_relative(entrypoint) or not entrypoint.endswith(".py"):
        raise ValueError("Mission entrypoint must be a relative ASCII .py path.")
    if not _valid_relative(lock_path) or lock_path in ("mission.toml", CONTEXT_FILE) or any(part.startswith(".") for part in lock_path.split("/")):
        raise ValueError("Mission lock_file must be a visible relative ASCII path.")
    profile = local.get("robot_profile_ref")
    if profile is not None and (not isinstance(profile, str) or not profile.strip() or len(profile) > 240):
        raise ValueError("robot_profile_ref must contain 1–240 characters.")
    try:
        files = {"requirements.lock": lock_path}
        for current, dirs, names in os.walk(directory, followlinks=False):
            dirs[:] = [name for name in dirs if not name.startswith(".")]
            for name in dirs:
                if (Path(current) / name).is_symlink():
                    raise ValueError("Mission entries must not be symlinks.")
            for name in names:
                if name.startswith("."):
                    continue
                path = Path(current) / name
                relative = path.relative_to(directory).as_posix()
                if relative in ("mission.toml", CONTEXT_FILE, lock_path):
                    continue
                if relative == "requirements.lock":
                    raise ValueError("requirements.lock conflicts with the selected lock_file.")
                if not _valid_relative(relative) or path.is_symlink():
                    raise ValueError("Mission contains an invalid path or symlink.")
                if len(files) >= 1024:
                    raise ValueError("Mission exceeds 1024 files.")
                files[relative] = relative
    except (OSError, UnicodeError) as exc:
        raise ValueError("Mission source or lock is unreadable or invalid UTF-8.") from exc
    if entrypoint == CONTEXT_FILE:
        raise ValueError("Mission entrypoint cannot be the local context file.")
    if entrypoint not in files:
        raise ValueError("Mission entrypoint is missing or excluded.")
    bundle = _pack_mission(directory, files, entrypoint, progress)
    manifest = {"schema_version": 1, "name": mission_name, "entrypoint": entrypoint,
                "python_requirement": requirement, "lock_file": "requirements.lock",
                "source_digest": bundle.revision_digest}
    if profile is not None:
        manifest["robot_profile_ref"] = profile
    return MissionBundle(bundle, manifest)
