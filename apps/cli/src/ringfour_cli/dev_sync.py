"""Build local development snapshots and connect to a pinned daemon."""
import base64
import hashlib
import hmac
import http.client
import io
import json
import os
import ssl
import tomllib
import zipfile
from datetime import UTC, datetime
from pathlib import Path
from urllib.parse import urlsplit

from cryptography import x509

from .mission_bundle import _read_under, _valid_relative
from .mission_context import CONTEXT_FILE
from .script_bundle import MAX_ARCHIVE_BYTES, MAX_FILE_BYTES, pack_bundle


class LocalConnectionError(ValueError):
    pass


class LocalTransportError(LocalConnectionError):
    pass


def daemon_url(value):
    try:
        parsed = urlsplit(value)
        port = parsed.port or 443
        if (parsed.scheme != "https" or not parsed.hostname or parsed.username is not None or parsed.password is not None
                or parsed.query or parsed.fragment or parsed.path not in ("", "/") or not 1 <= port <= 65535):
            raise ValueError
        return value.rstrip("/")
    except (TypeError, ValueError):
        raise ValueError("Daemon URL must be HTTPS without credentials, path, query or fragment.") from None


def local_request(url, certificate_sha256, token, method, path, *, body=None, key=None):
    """Pin and check certificate validity before transmitting any bearer token."""
    parsed = urlsplit(daemon_url(url))
    if not isinstance(certificate_sha256, str) or len(certificate_sha256) != 64 or any(c not in "0123456789abcdef" for c in certificate_sha256):
        raise LocalConnectionError("Platform returned an invalid daemon fingerprint.")
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE  # Exact pin and validity are checked before sending credentials.
    connection = http.client.HTTPSConnection(parsed.hostname, parsed.port or 443, context=context, timeout=15)
    try:
        connection.connect()
        der = connection.sock.getpeercert(binary_form=True)
        actual = hashlib.sha256(der).hexdigest()
        if not hmac.compare_digest(actual, certificate_sha256):
            raise LocalConnectionError("Daemon certificate fingerprint does not match the Platform.")
        cert = x509.load_der_x509_certificate(der)
        now = datetime.now(UTC)
        if not cert.not_valid_before_utc <= now < cert.not_valid_after_utc:
            raise LocalConnectionError("Daemon certificate is expired or not yet valid.")
        headers = {"Authorization": f"Bearer {token}", "Accept": "application/json"}
        data = None
        if body is not None:
            data = json.dumps(body, separators=(",", ":")).encode()
            headers["Content-Type"] = "application/json"
        if key is not None:
            headers["Idempotency-Key"] = key
        connection.request(method, path, body=data, headers=headers)
        response = connection.getresponse()
        payload = response.read(2 * 1048576 + 1)
        if len(payload) > 2 * 1048576:
            raise LocalConnectionError("Daemon response exceeds its limit.")
        try:
            value = json.loads(payload)
        except (ValueError, UnicodeError):
            raise LocalConnectionError("Daemon returned an invalid response.") from None
        return response.status, value
    except (OSError, ssl.SSLError, http.client.HTTPException) as exc:
        raise LocalTransportError("Could not connect to the local daemon over HTTPS.") from exc
    finally:
        connection.close()


def build_snapshot(directory: Path, prior_paths: list[str], generation: int):
    if directory.is_symlink() or not directory.is_dir():
        raise ValueError("Mission directory must be a directory, not a symlink.")
    try:
        manifest = tomllib.loads(_read_under(directory, "mission.toml", 65536).decode("utf-8"))
        required = {"schema_version", "name", "entrypoint", "python_requirement", "lock_file"}
        if not required <= set(manifest) or set(manifest) - required - {"robot_profile_ref"} or type(manifest["schema_version"]) is not int or manifest["schema_version"] != 1:
            raise ValueError("Invalid mission manifest.")
        entrypoint, lock_path, requirement = manifest["entrypoint"], manifest["lock_file"], manifest["python_requirement"]
        if not _valid_relative(entrypoint) or not entrypoint.endswith(".py") or not _valid_relative(lock_path) or lock_path in ("mission.toml", CONTEXT_FILE) or any(part.startswith(".") or part == "__pycache__" for part in lock_path.split("/")):
            raise ValueError("Invalid mission entrypoint or lock path.")
        if not isinstance(requirement, str) or not requirement.strip() or len(requirement) > 128:
            raise ValueError("Invalid Python requirement.")
        lock = _read_under(directory, lock_path, MAX_FILE_BYTES)
        lock.decode("utf-8")
        files = {"requirements.lock": lock}
        total = len(lock)
        for current, dirs, names in os.walk(directory, followlinks=False):
            dirs[:] = [name for name in dirs if not name.startswith(".") and name != "__pycache__"]
            if any((Path(current) / name).is_symlink() for name in dirs):
                raise ValueError("Mission entries must not be symlinks.")
            for name in names:
                if name.startswith("."):
                    continue
                path = Path(current) / name
                relative = path.relative_to(directory).as_posix()
                if relative in ("mission.toml", CONTEXT_FILE, lock_path):
                    continue
                if relative == "requirements.lock":
                    raise ValueError("requirements.lock conflicts with the selected lock file.")
                if not _valid_relative(relative) or path.is_symlink():
                    raise ValueError("Mission contains an invalid path or symlink.")
                if len(files) >= 1024:
                    raise ValueError("Mission exceeds 1024 files.")
                value = _read_under(directory, relative, MAX_FILE_BYTES)
                total += len(value)
                if total > MAX_ARCHIVE_BYTES:
                    raise ValueError("Mission exceeds the 1 MiB bundle limit.")
                files[relative] = value
        if entrypoint not in files:
            raise ValueError("Mission entrypoint is missing or excluded.")
        if type(generation) is not int or not 0 <= generation <= 20 or not isinstance(prior_paths, list) or any(not _valid_relative(item) for item in prior_paths) or len(set(prior_paths)) != len(prior_paths):
            raise ValueError("Daemon returned invalid source state.")
        bundle = pack_bundle(files, entrypoint)
        with zipfile.ZipFile(io.BytesIO(bundle.archive)) as archive:
            entries = [{"path": name, "size": len(value), "sha256": hashlib.sha256(value).hexdigest()}
                       for name in archive.namelist() for value in [archive.read(name)]]
        return {"base_generation": generation, "archive_sha256": bundle.archive_sha256,
                "archive_size": len(bundle.archive), "archive_base64": base64.b64encode(bundle.archive).decode(),
                "manifest": {"entrypoint": entrypoint, "python_requirement": requirement, "lock_sha256": bundle.lock_sha256,
                             "files": entries, "deleted_paths": sorted(set(prior_paths) - files.keys())}}
    except (OSError, UnicodeError, tomllib.TOMLDecodeError) as exc:
        raise ValueError("Mission source or manifest is unreadable or invalid.") from exc
