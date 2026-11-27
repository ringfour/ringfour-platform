"""CLI for App development sessions; authenticated control plane, pinned LAN data."""

import base64
import hashlib
import io
import json
import os
import re
import time
import uuid
import zipfile
from functools import wraps
from pathlib import Path
from typing import Annotated

import httpx
import typer

from . import auth
from .app_project import atomic_json
from .development_transport import LocalConnectionError, _pinned_connection, daemon_url

CONTEXT = Path(".ringfour/development.json")
EXCLUDED = {
    "models",
    "weights",
    "python-wheels",
    "node_modules",
    "__pycache__",
    "build",
    "dist",
}
MODEL_SUFFIXES = {".onnx", ".safetensors", ".gguf", ".pt", ".pth"}


def snapshot(directory, excluded=()):
    if directory.is_symlink() or not directory.is_dir():
        raise ValueError("Source must be a real directory")
    buffer = io.BytesIO()
    total = 0
    count = 0
    with zipfile.ZipFile(buffer, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for current, dirs, names in os.walk(directory, followlinks=False):
            dirs[:] = sorted(
                n
                for n in dirs
                if not n.startswith(".") and n not in EXCLUDED and n not in excluded
            )
            if any((Path(current) / n).is_symlink() for n in dirs):
                raise ValueError("Source directories cannot be symlinks")
            for name in sorted(names):
                path = Path(current) / name
                if (
                    name.startswith(".")
                    or name in excluded
                    or path.suffix in MODEL_SUFFIXES
                ):
                    continue
                if path.is_symlink() or not path.is_file():
                    raise ValueError("Source cannot contain links or special files")
                if path.stat().st_size > 8 * 1024 * 1024:
                    raise ValueError(
                        "Source file exceeds 8 MiB; include large assets in the image or --exclude them"
                    )
                content = path.read_bytes()
                total += len(content)
                count += 1
                if total > 32 * 1024 * 1024 or count > 1024:
                    raise ValueError("Source exceeds 32 MiB or 1024 files")
                relative = path.relative_to(directory).as_posix()
                if not re.fullmatch(r"[A-Za-z0-9_][A-Za-z0-9_./-]*", relative):
                    raise ValueError(
                        "Source paths must use letters, digits, underscores, dots, slashes and hyphens"
                    )
                info = zipfile.ZipInfo(relative, (1980, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                archive.writestr(info, content)
    if not count or len(buffer.getvalue()) > 16 * 1024 * 1024:
        raise ValueError("Source requires 1–1024 files and at most 16 MiB compressed")
    return buffer.getvalue()


def local(connection, method, path, *, raw=None, headers=None):
    url, pin, token = connection
    client = _pinned_connection(url, pin)
    try:
        client.request(
            method,
            path,
            body=raw,
            headers={
                "Authorization": "Bearer " + token,
                "Content-Type": "application/zip",
                **(headers or {}),
            },
        )
        response = client.getresponse()
        data = response.read(65537)
        if len(data) > 65536:
            raise ValueError("Local response exceeds limit")
        value = json.loads(data)
        if response.status != 200:
            raise LocalConnectionError(
                str(value.get("error", "Local operation failed"))
            )
        return value
    finally:
        client.close()


class Client:
    def __init__(self, settings, api):
        self.settings, self.api_call, self.store = settings, api, auth.TokenStore()

    def api(self, method, path, body=None, *, key=None):
        kwargs = {}
        if body is not None:
            kwargs["json"] = body
        if method == "POST":
            kwargs["headers"] = {"Idempotency-Key": key or str(uuid.uuid4())}
        response = self.api_call(method, path, self.settings, self.store, **kwargs)
        return None if response.status_code == 204 else response.json()

    def context(self):
        if CONTEXT.is_symlink() or CONTEXT.parent.is_symlink():
            raise ValueError("Development context cannot use symlinks")
        value = json.loads(CONTEXT.read_text())
        if value["api_url"].rstrip("/") != self.settings.api_url.rstrip("/"):
            raise ValueError("This development context belongs to another Platform")
        for key in ("robot_id", "session_id", "installation_id"):
            uuid.UUID(value[key])
        return value

    def remember(self, robot, session):
        CONTEXT.parent.mkdir(mode=0o700, exist_ok=True)
        value = {
            "api_url": self.settings.api_url,
            "robot_id": robot,
            "session_id": session["session_id"],
            "installation_id": session["installation_id"],
        }
        previous = json.loads(CONTEXT.read_text()) if CONTEXT.exists() else None
        if previous and previous.get("session_id") != session["session_id"]:
            (CONTEXT.parent / "development-start.json").unlink(missing_ok=True)
            (CONTEXT.parent / "development-sync.json").unlink(missing_ok=True)
        atomic_json(CONTEXT, value)
        return value

    def grant(self, context, override=None):
        raw = os.urandom(32)
        value = self.api(
            "POST",
            f"/v1/robots/{context['robot_id']}/app-development/grants",
            {
                "token_sha256": hashlib.sha256(raw).hexdigest(),
                "session_id": context["session_id"],
            },
        )
        if value["session"]["session_id"] != context["session_id"]:
            raise ValueError("Development session changed; connect again")
        connection = (
            daemon_url(override or value["daemon_url"]),
            value["certificate_sha256"],
            base64.urlsafe_b64encode(raw).decode().rstrip("="),
        )
        for _ in range(20):
            try:
                local(
                    connection,
                    "GET",
                    f"/v1/local/app-development/{context['session_id']}/source",
                )
                return connection
            except LocalConnectionError:
                time.sleep(0.5)
        raise ValueError(
            "The daemon has not installed the grant; check its development listener and Platform connection"
        )

    def wait(self, run):
        for _ in range(120):
            value = self.api("GET", f"/v1/app-runs/{run}")
            if value["state"] not in ("accepted", "running"):
                return value
            time.sleep(0.5)
        raise ValueError("Run is still stopping; retry after inspecting its state")


def start_once(client, context, body=None):
    """Resolve a lost start response with the same key before requesting another run."""
    pending = CONTEXT.parent / "development-start.json"
    if pending.exists():
        record = json.loads(pending.read_text())
        if (
            record["session_id"] != context["session_id"]
            or record["installation_id"] != context["installation_id"]
        ):
            raise ValueError(
                "Pending start belongs to another session; reconnect explicitly"
            )
    else:
        if body is None:
            raise ValueError("No pending start to recover")
        record = {
            "session_id": context["session_id"],
            "installation_id": context["installation_id"],
            "body": body,
            "key": str(uuid.uuid4()),
        }
        atomic_json(pending, record)
    value = client.api(
        "POST",
        f"/v1/app-installations/{context['installation_id']}/start",
        record["body"],
        key=record["key"],
    )
    context["run_id"] = value["run_id"]
    atomic_json(CONTEXT, context)
    pending.unlink()
    return value


def register(parent, settings, request, output=None):
    app = typer.Typer(
        help="Develop an App using direct source sync to its robot.",
        no_args_is_help=True,
    )
    parent.add_typer(app, name="dev")

    def client():
        return Client(settings(), request)

    def guarded(function):
        @wraps(function)
        def invoke(*args, **kwargs):
            try:
                return function(*args, **kwargs)
            except (OSError, ValueError, httpx.HTTPError) as error:
                typer.echo("Development request failed: " + str(error), err=True)
                raise typer.Exit(1) from error

        return invoke

    @app.command("enable")
    @guarded
    def enable(installation: str, ttl: int = typer.Option(3600, min=300, max=28800)):
        """Reserve development mode for one installed development App image."""
        c = client()
        installation = str(uuid.UUID(installation))
        binding = c.api("GET", "/v1/app-installations/" + installation)
        robot = binding["robot_id"]
        session = c.api(
            "POST",
            f"/v1/robots/{robot}/app-development",
            {"installation_id": installation, "ttl_seconds": ttl},
        )
        c.remember(robot, session)
        typer.echo("Development session: " + session["session_id"])

    @app.command("connect")
    @guarded
    def connect(robot: str = typer.Option(..., "--robot")):
        """Connect this project to development mode enabled in Studio."""
        c = client()
        robot = str(uuid.UUID(robot))
        session = c.api("GET", f"/v1/robots/{robot}/app-development")["session"]
        if not session:
            raise ValueError("Enable development mode in Studio first")
        context = {"robot_id": robot, "session_id": session["session_id"]}
        c.grant(context)
        c.remember(robot, session)
        typer.echo("Connected. Run app dev sync, then app dev restart.")

    @app.command("sync")
    @guarded
    def sync(
        source: Annotated[Path, typer.Option("--source")] = Path("."),
        exclude: Annotated[list[str] | None, typer.Option("--exclude")] = None,
        daemon: str | None = typer.Option(None, "--daemon-url"),
    ):
        """Sync source directly over pinned HTTPS; models/dependencies remain in the image."""
        c = client()
        context = c.context()
        connection = c.grant(context, daemon)
        path = f"/v1/local/app-development/{context['session_id']}/source"
        state = local(connection, "GET", path)
        raw = snapshot(source, exclude or ())
        digest = "sha256:" + hashlib.sha256(raw).hexdigest()
        pending = CONTEXT.parent / "development-sync.json"
        if state["archive_sha256"] == digest:
            result = state
        else:
            headers = {
                "X-Archive-Sha256": digest,
                "X-Base-Generation": str(state["generation"]),
                "Idempotency-Key": str(uuid.uuid4()),
            }
            # Persist the exact retry identity before sending, never the local token.
            pending = CONTEXT.parent / "development-sync.json"
            record = {
                "session_id": context["session_id"],
                "digest": digest,
                "headers": headers,
            }
            if pending.exists():
                previous = json.loads(pending.read_text())
                if (
                    previous.get("digest") == digest
                    and previous.get("session_id") == context["session_id"]
                ):
                    headers = previous["headers"]
            atomic_json(pending, record | {"headers": headers})
            result = local(connection, "PUT", path, raw=raw, headers=headers)
        for _ in range(40):
            hosted = c.api("GET", f"/v1/robots/{context['robot_id']}/app-development")[
                "session"
            ]
            if (
                hosted
                and hosted["session_id"] == context["session_id"]
                and hosted["generation"] == result["generation"]
                and hosted["archive_sha256"] == digest
            ):
                pending.unlink(missing_ok=True)
                typer.echo(
                    f"Source generation {result['generation']} synced; image unchanged"
                )
                return
            time.sleep(0.5)
        raise ValueError(
            "Source committed locally; Platform report is pending. Wait before restarting"
        )

    @app.command("restart")
    @guarded
    def restart(follow: bool = typer.Option(False, "--follow", "-f")):
        """Stop this session's previous run and start a new source-attributed App run."""
        c = client()
        context = c.context()
        if (CONTEXT.parent / "development-start.json").exists():
            value = start_once(c, context)
            typer.echo("Recovered development App run: " + value["run_id"])
            if follow:
                logs(follow=True)
            return
        installation = c.api(
            "GET", f"/v1/app-installations/{context['installation_id']}"
        )
        runs = c.api(
            "GET",
            f"/v1/organizations/{installation['organization_id']}/app-runs?installation_id={context['installation_id']}&active_only=true",
        )["items"]
        for run in runs:
            if (
                run["snapshot"].get("development", {}).get("session_id")
                != context["session_id"]
            ):
                raise ValueError("Another App execution owns this robot")
            c.api(
                "POST",
                f"/v1/app-installations/{context['installation_id']}/stop",
                {"expected_run_id": run["id"]},
            )
            c.wait(run["id"])
        binding = installation["installed"]
        value = start_once(
            c,
            context,
            {
                "expected_revision_id": binding["revision_id"],
                "expected_configuration_revision_id": binding[
                    "configuration_revision_id"
                ],
                "development_session_id": context["session_id"],
            },
        )
        context["run_id"] = value["run_id"]
        atomic_json(CONTEXT, context)
        typer.echo("Development App run: " + value["run_id"])
        if follow:
            logs(follow=True)

    @app.command("logs")
    @guarded
    def logs(follow: bool = typer.Option(False, "--follow", "-f")):
        """Read regular durable App logs for this project's last development run."""
        c = client()
        context = c.context()
        run = context.get("run_id")
        if not run:
            raise ValueError("No development run recorded; run app dev restart first")
        if output is not None:
            return output(run, follow=follow)
        offsets = {"stdout": 0, "stderr": 0}
        while True:
            for stream, offset in offsets.items():
                value = c.api(
                    "GET",
                    f"/v1/app-runs/{run}/output?stream={stream}&offset={offset}&limit=16384",
                )
                data = base64.b64decode(value.get("data_base64", ""))
                typer.echo(
                    data.decode(errors="replace"), nl=False, err=stream == "stderr"
                )
                offsets[stream] = offset + len(data)
            state = c.api("GET", f"/v1/app-runs/{run}")
            if not follow or state["state"] not in ("accepted", "running"):
                return
            time.sleep(0.5)

    @app.command("status")
    @guarded
    def status():
        c = client()
        context = c.context()
        typer.echo(
            json.dumps(
                c.api("GET", f"/v1/robots/{context['robot_id']}/app-development"),
                indent=2,
            )
        )

    @app.command("stop")
    @guarded
    def stop():
        """Close development mode; the daemon stops this session's active run."""
        c = client()
        context = c.context()
        c.api(
            "DELETE",
            f"/v1/robots/{context['robot_id']}/app-development?expected_session_id={context['session_id']}",
        )
        typer.echo("Development mode ended; daemon cleanup is pending until observed")
