"""Capture bounded immutable artifact snapshots for completed executions."""
import hashlib
import json
import os
import shutil
import stat
from pathlib import Path

MAX_FILES = 16
MAX_FILE_BYTES = 262144
MAX_TOTAL_BYTES = 1048576


class ArtifactError(Exception):
    pass


def valid_path(name):
    if not isinstance(name, str) or not name or "\\" in name or any(ord(c) < 32 or ord(c) == 127 for c in name):
        return False
    try:
        if len(name.encode("utf-8")) > 240:
            return False
    except UnicodeError:
        return False
    return not name.startswith("/") and all(part not in ("", ".", "..") for part in name.split("/"))


def open_regular_under(root, relative):
    """Open through directory descriptors so parent symlink swaps fail closed."""
    directory_flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
    current = os.open(root, directory_flags)
    try:
        parts = relative.split("/")
        for part in parts[:-1]:
            next_dir = os.open(part, directory_flags, dir_fd=current)
            os.close(current)
            current = next_dir
        descriptor = os.open(parts[-1], os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=current)
        try:
            if not stat.S_ISREG(os.fstat(descriptor).st_mode):
                raise ArtifactError("Only regular files are permitted")
            return os.fdopen(descriptor, "rb")
        except BaseException:
            os.close(descriptor)
            raise
    finally:
        os.close(current)


def collect(root, destination):
    """Copy and hash a bounded snapshot; publish it only when fully valid."""
    root = Path(root)
    destination = Path(destination)
    pending = destination.with_name(destination.name + ".pending")
    shutil.rmtree(pending, ignore_errors=True)
    pending.mkdir(parents=True)
    entries = []
    total = 0
    try:
        if root.is_symlink() or not root.is_dir():
            raise ArtifactError("Invalid artifact directory")
        for current, dirs, files in os.walk(root, followlinks=False):
            for name in sorted(dirs + files):
                path = Path(current) / name
                mode = path.lstat().st_mode
                if stat.S_ISDIR(mode):
                    continue
                if not stat.S_ISREG(mode):
                    raise ArtifactError("Only regular files and directories are permitted")
                relative = path.relative_to(root).as_posix()
                if not valid_path(relative) or len(entries) >= MAX_FILES:
                    raise ArtifactError("Invalid artifact path or file count")
                with open_regular_under(root, relative) as source:
                    data = source.read(MAX_FILE_BYTES + 1)
                total += len(data)
                if len(data) > MAX_FILE_BYTES or total > MAX_TOTAL_BYTES:
                    raise ArtifactError("Artifact byte limit exceeded")
                target = pending / "files" / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(data)
                entries.append({"path": relative, "size": len(data), "sha256": hashlib.sha256(data).hexdigest()})
        entries.sort(key=lambda item: item["path"])
        (pending / "manifest.json").write_text(json.dumps(entries, separators=(",", ":")), encoding="utf-8")
        pending.replace(destination)
        return entries
    except (OSError, ValueError, UnicodeError, ArtifactError) as exc:
        shutil.rmtree(pending, ignore_errors=True)
        raise ArtifactError("Artifact snapshot could not be collected") from exc


def listing(destination):
    manifest = Path(destination) / "manifest.json"
    return json.loads(manifest.read_text(encoding="utf-8")) if manifest.exists() else []
