"""Parse and deterministically package an immutable mission directory."""
import os
import re
import stat
import tomllib
from dataclasses import dataclass
from pathlib import Path

from .script_bundle import MAX_ARCHIVE_BYTES, MAX_FILE_BYTES, ScriptBundle, pack_bundle


@dataclass(frozen=True)
class MissionBundle:
    bundle: ScriptBundle
    manifest: dict


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


def build_mission_bundle(directory: Path) -> MissionBundle:
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
    if not _valid_relative(lock_path) or lock_path == "mission.toml" or any(part.startswith(".") for part in lock_path.split("/")):
        raise ValueError("Mission lock_file must be a visible relative ASCII path.")
    profile = local.get("robot_profile_ref")
    if profile is not None and (not isinstance(profile, str) or not profile.strip() or len(profile) > 240):
        raise ValueError("robot_profile_ref must contain 1–240 characters.")
    try:
        lock = _read_under(directory, lock_path, MAX_FILE_BYTES)
        lock.decode("utf-8")
        files = {"requirements.lock": lock}
        total = len(lock)
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
                if relative in ("mission.toml", lock_path):
                    continue
                if relative == "requirements.lock":
                    raise ValueError("requirements.lock conflicts with the selected lock_file.")
                if not _valid_relative(relative) or path.is_symlink():
                    raise ValueError("Mission contains an invalid path or symlink.")
                if len(files) >= 1024:
                    raise ValueError("Mission exceeds 1024 files.")
                content = _read_under(directory, relative, MAX_FILE_BYTES)
                total += len(content)
                if total > MAX_ARCHIVE_BYTES:
                    raise ValueError("Mission exceeds the 1 MiB bundle limit.")
                files[relative] = content
    except (OSError, UnicodeError) as exc:
        raise ValueError("Mission source or lock is unreadable or invalid UTF-8.") from exc
    if entrypoint not in files:
        raise ValueError("Mission entrypoint is missing or excluded.")
    bundle = pack_bundle(files, entrypoint)
    manifest = {"schema_version": 1, "name": mission_name, "entrypoint": entrypoint,
                "python_requirement": requirement, "lock_file": "requirements.lock",
                "source_digest": bundle.revision_digest}
    if profile is not None:
        manifest["robot_profile_ref"] = profile
    return MissionBundle(bundle, manifest)
