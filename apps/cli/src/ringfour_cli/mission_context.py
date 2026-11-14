"""Disposable, local mission target context; the Platform remains authoritative."""
import json
import os
import tempfile
from pathlib import Path
from urllib.parse import urlsplit

CONTEXT_FILE = "ringfour.context.json"
DEV_FIELDS = {"api_url", "organization_id", "robot_id", "robot_name", "session_id", "daemon_url"}
FIELDS = {"api_url", "organization_id", "mission_name", "mission_id", "robot_id", "robot_name", "deployment_id", "revision_id"}


def context_api_url(url: str) -> str:
    """Keep credentials and query parameters out of a shareable context file."""
    try:
        parsed = urlsplit(url)
        if parsed.scheme not in ("http", "https") or not parsed.hostname or parsed.username is not None or parsed.password is not None or parsed.query or parsed.fragment:
            raise ValueError
        _ = parsed.port
    except ValueError as exc:
        raise ValueError("API URL must be HTTP(S) without credentials, query parameters or a fragment for local mission context.") from exc
    return url.rstrip("/")


def mission_directory(start: Path) -> Path:
    current = start.resolve()
    for path in (current, *current.parents):
        if (path / "mission.toml").is_file():
            return path
    raise ValueError("No mission.toml found in this directory or its parents.")


def read_context(directory: Path) -> dict:
    path = directory / CONTEXT_FILE
    if not path.exists() and not path.is_symlink():
        return {"version": 1, "targets": []}
    if path.is_symlink() or not path.is_file():
        raise ValueError(f"{CONTEXT_FILE} must be a regular file, not a symlink.")
    try:
        if path.stat().st_size > 1024 * 1024:
            raise ValueError("Context file is too large.")
        document = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(document, dict) or not {"version", "targets"} <= set(document) or set(document) - {"version", "targets", "dev_sessions"} or type(document["version"]) is not int or document["version"] != 1 or not isinstance(document["targets"], list) or not isinstance(document.get("dev_sessions", []), list):
            raise ValueError("Invalid context structure.")
        for item in document["targets"]:
            if not isinstance(item, dict) or set(item) != FIELDS or any(not isinstance(item[field], str) or not item[field] for field in FIELDS - {"robot_name"}) or (item["robot_name"] is not None and not isinstance(item["robot_name"], str)):
                raise ValueError("Invalid target.")
        for item in document.get("dev_sessions", []):
            if not isinstance(item, dict) or set(item) != DEV_FIELDS or any(not isinstance(item[field], str) or not item[field] for field in DEV_FIELDS - {"robot_name", "daemon_url"}) or any(item[field] is not None and not isinstance(item[field], str) for field in ("robot_name", "daemon_url")):
                raise ValueError("Invalid development session.")
        return document
    except (OSError, UnicodeError, json.JSONDecodeError, ValueError) as exc:
        raise ValueError(f"Invalid {CONTEXT_FILE}: {exc}") from exc


def save_target(directory: Path, target: dict) -> None:
    document = read_context(directory)
    identity = (target["api_url"], target["organization_id"], target["robot_id"])
    document["targets"] = [item for item in document["targets"] if (item["api_url"], item["organization_id"], item["robot_id"]) != identity]
    document["targets"].append(target)
    _write_context(directory, document)


def save_dev_session(directory: Path, target: dict) -> None:
    document = read_context(directory)
    identity = (target["api_url"], target["organization_id"], target["robot_id"])
    sessions = document.get("dev_sessions", [])
    document["dev_sessions"] = [item for item in sessions if (item["api_url"], item["organization_id"], item["robot_id"]) != identity]
    document["dev_sessions"].append(target)
    _write_context(directory, document)


def _write_context(directory: Path, document: dict) -> None:
    filename = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=directory, prefix=".ringfour-context-", delete=False) as output:
            filename = output.name
            json.dump(document, output, indent=2)
            output.write("\n")
            output.flush()
            os.fsync(output.fileno())
        os.replace(filename, directory / CONTEXT_FILE)
    finally:
        if filename and os.path.exists(filename):
            os.unlink(filename)
