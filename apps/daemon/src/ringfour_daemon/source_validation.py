"""Daemon-side independent validation for local source snapshots."""
import hashlib
import io
import re
import stat
import zipfile

from packaging.specifiers import InvalidSpecifier, SpecifierSet

MAX_ARCHIVE = 1048576
MAX_FILE = 262144
MAX_FILES = 1024
_DIGEST = re.compile(r"[0-9a-f]{64}\Z")
_COMPONENT = re.compile(r"[A-Za-z0-9_.-]+\Z")


def valid_path(name):
    return isinstance(name, str) and 0 < len(name) <= 512 and all(
        part not in ("", ".", "..", "__pycache__") and not part.startswith(".") and _COMPONENT.fullmatch(part)
        for part in name.split("/")
    )


def digest(data):
    return hashlib.sha256(data).hexdigest()


def validate_snapshot(archive_bytes, manifest):
    if not isinstance(manifest, dict) or set(manifest) != {"entrypoint", "python_requirement", "lock_sha256", "files", "deleted_paths"}:
        raise ValueError("Invalid manifest")
    entrypoint, requirement, lock, listing, deletions = (manifest[key] for key in ("entrypoint", "python_requirement", "lock_sha256", "files", "deleted_paths"))
    if (not valid_path(entrypoint) or not entrypoint.endswith(".py") or not isinstance(requirement, str)
            or not 1 <= len(requirement) <= 128 or not isinstance(lock, str) or not _DIGEST.fullmatch(lock)
            or not isinstance(listing, list) or len(listing) > MAX_FILES or not isinstance(deletions, list)
            or len(deletions) > MAX_FILES or any(not valid_path(name) for name in deletions) or len(set(deletions)) != len(deletions)):
        raise ValueError("Invalid manifest")
    try:
        SpecifierSet(requirement)
    except InvalidSpecifier:
        raise ValueError("Invalid Python requirement") from None
    declared = {}
    for item in listing:
        if (not isinstance(item, dict) or set(item) != {"path", "size", "sha256"} or not valid_path(item["path"])
                or type(item["size"]) is not int or not 0 <= item["size"] <= MAX_FILE
                or not isinstance(item["sha256"], str) or not _DIGEST.fullmatch(item["sha256"])
                or item["path"] in declared):
            raise ValueError("Invalid file manifest")
        declared[item["path"]] = item
    if set(deletions) & declared.keys():
        raise ValueError("Deleted file still present")
    files, directories, explicit = {}, set(), set()
    total = 0
    try:
        with zipfile.ZipFile(io.BytesIO(archive_bytes)) as archive:
            if len(archive.infolist()) > MAX_FILES * 2:
                raise ValueError("Too many entries")
            for entry in archive.infolist():
                directory = entry.is_dir()
                name = entry.filename[:-1] if directory else entry.filename
                mode = (entry.external_attr >> 16) & 0o170000
                if not valid_path(name) or name in explicit or mode not in (0, stat.S_IFDIR if directory else stat.S_IFREG):
                    raise ValueError("Invalid ZIP entry")
                explicit.add(name)
                components = name.split("/")
                for index in range(1, len(components)):
                    parent = "/".join(components[:index])
                    if parent in files:
                        raise ValueError("File and directory collide")
                    directories.add(parent)
                if directory:
                    if name in files:
                        raise ValueError("File and directory collide")
                    directories.add(name)
                    continue
                if name in directories or len(files) >= MAX_FILES or entry.file_size > MAX_FILE:
                    raise ValueError("Invalid bundle size or path")
                with archive.open(entry) as source:
                    data = source.read(MAX_FILE + 1)
                total += len(data)
                if len(data) != entry.file_size or len(data) > MAX_FILE or total > MAX_ARCHIVE:
                    raise ValueError("Bundle size limit")
                files[name] = data
    except (OSError, zipfile.BadZipFile, RuntimeError, EOFError) as exc:
        raise ValueError("Invalid archive") from exc
    if set(files) != set(declared) or entrypoint not in files or "requirements.lock" not in files:
        raise ValueError("Archive does not match manifest")
    for name, data in files.items():
        if len(data) != declared[name]["size"] or digest(data) != declared[name]["sha256"]:
            raise ValueError("File digest mismatch")
    if digest(files["requirements.lock"]) != lock:
        raise ValueError("Lock digest mismatch")
    try:
        files["requirements.lock"].decode("utf-8")
    except UnicodeError:
        raise ValueError("Invalid lock encoding") from None
    return files
