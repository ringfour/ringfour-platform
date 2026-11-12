"""Build a bounded immutable single-script revision for the upload API."""
import hashlib
import io
import json
import re
import zipfile
from dataclasses import dataclass
from pathlib import Path

MAX_FILE_BYTES = 262144
MAX_ARCHIVE_BYTES = 1048576


def _sha256(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


@dataclass(frozen=True)
class ScriptBundle:
    archive: bytes
    archive_sha256: str
    revision_digest: str
    entrypoint: str
    lock_sha256: str

    def revision_request(self, organization_id: str, python_requirement: str) -> dict:
        return {
            "organization_id": organization_id,
            "archive_sha256": self.archive_sha256,
            "archive_size": len(self.archive),
            "revision_digest": self.revision_digest,
            "entrypoint": self.entrypoint,
            "python_requirement": python_requirement,
            "lock_sha256": self.lock_sha256,
        }


def pack_bundle(files: dict[str, bytes], entrypoint: str) -> ScriptBundle:
    lock = files["requirements.lock"]
    try:
        lock.decode("utf-8")
    except UnicodeError as exc:
        raise ValueError("Dependency lock must be UTF-8.") from exc
    manifest = [[path, _sha256(data)] for path, data in sorted(files.items())]
    digest = _sha256(json.dumps(manifest, ensure_ascii=False, separators=(",", ":")).encode())
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for path, data in sorted(files.items()):
            # Stable metadata gives matching retries the same archive digest.
            info = zipfile.ZipInfo(path, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, data)
    value = stream.getvalue()
    if len(value) > MAX_ARCHIVE_BYTES:
        raise ValueError("Compressed archive exceeds 1 MiB.")
    return ScriptBundle(value, _sha256(value), digest, entrypoint, _sha256(lock))


def build_script_bundle(script: Path, lockfile: Path | None = None) -> ScriptBundle:
    name = script.name
    if not re.fullmatch(r"[A-Za-z0-9_.-]+\.py", name) or len(name) > 240:
        raise ValueError("Script filename must be an ASCII .py filename.")
    if script.is_symlink() or not script.is_file():
        raise ValueError("Script must be a regular file, not a symlink.")
    if lockfile is not None and (lockfile.is_symlink() or not lockfile.is_file()):
        raise ValueError("Dependency lock must be a regular file, not a symlink.")
    with script.open("rb") as source:
        code = source.read(MAX_FILE_BYTES + 1)
    if lockfile:
        with lockfile.open("rb") as source:
            lock = source.read(MAX_FILE_BYTES + 1)
    else:
        lock = b""
    if len(code) > MAX_FILE_BYTES or len(lock) > MAX_FILE_BYTES:
        raise ValueError("Script or lock exceeds 256 KiB.")
    return pack_bundle({name: code, "requirements.lock": lock}, name)
