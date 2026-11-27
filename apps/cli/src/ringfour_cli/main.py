import base64
import codecs
import hashlib
import json
import os
import subprocess
import tempfile
import time
import uuid
import webbrowser
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path
from typing import Annotated
from urllib.parse import urlencode

import httpx
import typer
from keyring.errors import KeyringError
from rich.console import Console
from rich.table import Table

from . import app_project, auth
from .app_manifest import fingerprint as app_fingerprint
from .app_manifest import read_json as read_app_json
from .app_manifest import validate_authoring, validate_manifest
from .config import (
    ConfigError,
    load_settings,
    require_auth0,
    selected_org,
    update_config,
)
from .development_commands import register as register_app_development

app = typer.Typer(
    name="ringfour-cli",
    help="Ringfour developer CLI.",
    no_args_is_help=True,
)
org_app = typer.Typer(help="Manage organizations.", no_args_is_help=True)
app.add_typer(org_app, name="org")
robot_app = typer.Typer(help="Manage robots, credentials and observed App capabilities.", no_args_is_help=True)
app.add_typer(robot_app, name="robot")
daemon_app = typer.Typer(help="Drive the local device daemon.", no_args_is_help=True)
app.add_typer(daemon_app, name="daemon")
apps_app = typer.Typer(help="Discover, import, install and operate immutable Apps.", no_args_is_help=True)
app.add_typer(apps_app, name="app")
app_settings_app = typer.Typer(help="Inspect and change live App settings.", no_args_is_help=True)
apps_app.add_typer(app_settings_app, name="settings")
hitl_app = typer.Typer(help="Inspect and answer human interactions.", no_args_is_help=True)
app.add_typer(hitl_app, name="hitl")
console = Console()
publish_console = Console(stderr=True)

# name -> (env var, default base URL)
SERVICES: tuple[tuple[str, str, str], ...] = (
    ("api", "RINGFOUR_API_URL", "http://localhost:8080"),
    ("daemon", "RINGFOUR_DAEMON_URL", "http://localhost:8081"),
)


@dataclass(frozen=True)
class ServiceHealth:
    name: str
    url: str
    reachable: bool
    status: str
    version: str
    checks: dict[str, str]


def probe(client: httpx.Client, name: str, base_url: str) -> ServiceHealth:
    url = f"{base_url}/health/ready"
    try:
        response = client.get(url)
        body = response.json()
        return ServiceHealth(
            name=name,
            url=base_url,
            reachable=True,
            status=body.get("status", "fail") if response.status_code == 200 else "fail",
            version=body.get("version", "?"),
            checks=body.get("checks", {}),
        )
    except (httpx.HTTPError, ValueError):
        return ServiceHealth(
            name=name, url=base_url, reachable=False, status="fail", version="?", checks={}
        )


def collect_health(
    client_factory: Callable[[], httpx.Client],
    base_urls: dict[str, str] | None = None,
) -> list[ServiceHealth]:
    base_urls = base_urls or {
        name: os.environ.get(env, default) for name, env, default in SERVICES
    }
    with client_factory() as client:
        return [probe(client, name, url) for name, url in base_urls.items()]


@app.callback()
def callback() -> None:
    """Ringfour developer CLI."""


@app.command()
def scaffold(
    destination: Annotated[
        Path, typer.Argument(help="Directory for the new checkout.")
    ] = Path("my-ringfour-app"),
    repo: Annotated[
        str | None,
        typer.Option(
            "--repo",
            envvar="RINGFOUR_TEMPLATE_REPO",
            help="Template Git URL or local repository path.",
        ),
    ] = None,
) -> None:
    """Clone the Ringfour App template into a new or empty directory."""
    if repo is None:
        repo = "https://github.com/ringfour/ringfour-template.git"
        for parent in Path(__file__).resolve().parents:
            if parent.name == "ringfour-platform":
                sibling = parent.with_name("ringfour-template")
                if (sibling / ".git").exists():
                    repo = str(sibling)
                break
    try:
        subprocess.run(["git", "clone", "--", repo, str(destination)], check=True)
    except FileNotFoundError as error:
        typer.echo("Install Git to use ringfour-cli scaffold.", err=True)
        raise typer.Exit(code=1) from error
    except subprocess.CalledProcessError as error:
        raise typer.Exit(code=1) from error


def _default_client_factory() -> httpx.Client:
    return httpx.Client(timeout=3.0)


@app.command()
def health() -> None:
    """Check readiness of the configured Ringfour services."""
    results = collect_health(_default_client_factory)

    table = Table(title="Ringfour service health")
    table.add_column("Service")
    table.add_column("URL")
    table.add_column("Status")
    table.add_column("Version")
    table.add_column("Checks")
    for result in results:
        status = "[green]ok[/green]" if result.status == "ok" else "[red]fail[/red]"
        checks = ", ".join(f"{k}={v}" for k, v in result.checks.items()) or "-"
        if not result.reachable:
            checks = "unreachable"
        table.add_row(result.name, result.url, status, result.version, checks)
    console.print(table)

    if any(result.status != "ok" for result in results):
        raise typer.Exit(code=1)


def _settings():
    settings = load_settings()
    try:
        require_auth0(settings)
    except ConfigError as error:
        console.print(f"[red]{error}[/red]")
        raise typer.Exit(code=1) from error
    return settings


def _api_request(
    method: str,
    path: str,
    settings,
    store: auth.TokenStore,
    **kwargs,
) -> httpx.Response:
    error_json = kwargs.pop("_error_json", False)
    tokens = store.load(settings)
    if tokens is None:
        if error_json:
            typer.echo(
                json.dumps(
                    {
                        "error": {
                            "code": "unauthenticated",
                            "message": "Run ringfour-cli login first.",
                            "details": {"retryable": False},
                        }
                    }
                )
            )
        else:
            console.print("[red]Not logged in. Run `ringfour-cli login` first.[/red]")
        raise typer.Exit(code=1)
    extra_headers = kwargs.pop("headers", {})
    request_timeout = kwargs.pop("_timeout", 15.0)
    with httpx.Client(base_url=settings.api_url, timeout=request_timeout) as client:
        response = client.request(
            method,
            path,
            headers={
                **extra_headers,
                "Authorization": f"Bearer {tokens['access_token']}",
            },
            **kwargs,
        )
        if response.status_code == 401:
            try:
                tokens = auth.refresh_tokens(client, settings, tokens)
            except auth.DeviceAuthError as error:
                if error_json:
                    typer.echo(
                        json.dumps(
                            {
                                "error": {
                                    "code": "unauthenticated",
                                    "message": "Refresh failed; run ringfour-cli login.",
                                    "details": {"retryable": False},
                                }
                            }
                        )
                    )
                else:
                    console.print(f"[red]{error}[/red]")
                raise typer.Exit(code=1) from error
            store.save(settings, tokens)
            response = client.request(
                method,
                path,
                headers={
                    **extra_headers,
                    "Authorization": f"Bearer {tokens['access_token']}",
                },
                **kwargs,
            )
    if response.status_code >= 400:
        if error_json:
            try:
                payload = response.json()
                if not isinstance(payload.get("error"), dict):
                    raise TypeError
            except (ValueError, AttributeError, TypeError):
                payload = {
                    "error": {
                        "code": "api_error",
                        "message": f"API returned HTTP {response.status_code}.",
                        "details": {"retryable": response.status_code >= 500},
                    }
                }
            typer.echo(json.dumps(payload))
            raise typer.Exit(code=1)
        try:
            error = response.json()["error"]
            console.print(f"[red]{response.status_code} {error['code']}: {error['message']}[/red]")
        except (ValueError, KeyError):
            console.print(f"[red]Request failed with status {response.status_code}.[/red]")
        raise typer.Exit(code=1)
    return response


def _app_result(action, json_output):
    try:
        value = action()
    except (
        ValueError,
        OSError,
        KeyError,
        ConfigError,
        httpx.HTTPError,
        auth.DeviceAuthError,
        KeyringError,
    ) as error:
        message = (
            "API transport failed; verify the API URL and connection."
            if isinstance(error, httpx.HTTPError)
            else str(error)[:1000]
        )
        code = (
            "build_failed"
            if isinstance(error, app_project.DockerError)
            else "invalid_input"
        )
        if isinstance(error, httpx.HTTPError):
            code = "api_unavailable"
        if isinstance(error, (auth.DeviceAuthError, KeyringError)):
            code = "unauthenticated"
        if json_output:
            typer.echo(
                json.dumps(
                    {
                        "error": {
                            "code": code,
                            "message": message,
                            "details": {
                                "retryable": isinstance(
                                    error, (app_project.DockerError, httpx.HTTPError)
                                )
                            },
                        }
                    }
                )
            )
        else:
            typer.echo(f"{code}: {message}", err=True)
        raise typer.Exit(code=1) from error
    if json_output:
        typer.echo(json.dumps(value, ensure_ascii=False))
    else:
        console.print_json(data=value)


@apps_app.command("init")
def app_init(
    destination: Path,
    app_key: Annotated[str, typer.Option("--app-key")],
    repository: Annotated[str, typer.Option("--repository")],
    template_archive: Annotated[Path | None, typer.Option("--template-archive")] = None,
    json_output: Annotated[bool, typer.Option("--json")] = False,
):
    """Create a project from the pinned standalone Robot Check template."""
    _app_result(
        lambda: app_project.init_project(
            destination.absolute(), app_key, repository, template_archive
        ),
        json_output,
    )


@apps_app.command("validate")
def app_validate(
    directory: Annotated[Path, typer.Argument()] = Path("."),
    manifest: Annotated[Path | None, typer.Option("--manifest")] = None,
    json_output: Annotated[bool, typer.Option("--json")] = False,
):
    """Validate editable authoring input or a finalized immutable manifest."""

    def action():
        if manifest is not None:
            value = validate_manifest(read_app_json(manifest))
            return {
                "valid": True,
                "kind": "manifest",
                "manifest_digest": "sha256:" + app_fingerprint(value),
            }
        value = validate_authoring(read_app_json(directory / "app.json"))
        source, _ = app_project.source_snapshot(directory.resolve(), value)
        return {
            "valid": True,
            "kind": "authoring",
            "app_key": value["app_key"],
            "release": value["release"],
            "source_fingerprint": source,
        }

    _app_result(action, json_output)


def _app_config(path: Path | None, defaults: dict | None = None) -> dict:
    if path is None:
        return dict(defaults or {})
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise typer.BadParameter(f"Cannot read JSON configuration: {error}") from error
    if not isinstance(value, dict):
        raise typer.BadParameter("Configuration JSON must be an object.")
    return value


def _app_id(value: str) -> str:
    try:
        return str(uuid.UUID(value))
    except ValueError as error:
        raise typer.BadParameter("Use the App or release UUID shown by `app search`.") from error


def _app_target_robot(value: str, organization: str, settings, store) -> str:
    """Resolve a robot only from the selected workspace's visible inventory."""
    robots = list(_pages(f"/v1/organizations/{organization}/robots", settings, store))
    matches = [item for item in robots if value in (item.get("id"), item.get("name"))]
    if len(matches) != 1:
        raise typer.BadParameter("Robot is not visible or is ambiguous in this workspace; use its workspace-scoped ID.")
    return matches[0]["id"]


@apps_app.command("search")
def app_search(query: str | None = typer.Argument(None), org: str | None = typer.Option(None, "--org"), json_output: bool = typer.Option(False, "--json")):
    """Search listed Apps visible to the selected workspace."""
    settings, store = _settings(), auth.TokenStore()
    organization = _resolve_org(settings, org)
    path = "/v1/store/apps?" + urlencode({"organization_id": organization, **({"q": query} if query else {})})
    items = list(_pages(path, settings, store))
    if json_output:
        console.print_json(data={"items": items})
        return
    table = Table(title="App Store")
    for heading in ("App ID", "Name", "Key", "Publisher"):
        table.add_column(heading)
    for item in items:
        table.add_row(item["id"], item["name"], item["app_key"], item["publisher_organization_id"])
    console.print(table)


@apps_app.command("show")
def app_show(app_id: str):
    """Show an App and every visible immutable release."""
    settings, store = _settings(), auth.TokenStore()
    identifier = _app_id(app_id)
    app_record = _api_request("GET", f"/v1/apps/{identifier}", settings, store).json()
    revisions = list(_pages(f"/v1/apps/{identifier}/revisions", settings, store))
    console.print_json(data={"app": app_record, "revisions": revisions})


@apps_app.command("import")
def app_import(revision_id: str, org: str | None = typer.Option(None, "--org"), share_id: str | None = typer.Option(None, "--share-id"), idempotency_key: str | None = typer.Option(None, "--idempotency-key"), json_output: bool = typer.Option(False, "--json")):
    """Import a listed release or a destination-bound direct share."""
    settings, store = _settings(), auth.TokenStore()
    organization = _resolve_org(settings, org)
    body = {"revision_id": _app_id(revision_id)}
    if share_id:
        body["share_id"] = _app_id(share_id)
    result = _api_request("POST", f"/v1/organizations/{organization}/app-imports", settings, store,
        headers={"Idempotency-Key": _idempotency_key(idempotency_key)}, json=body, _error_json=json_output).json()
    console.print_json(data=result)


@apps_app.command("imports")
def app_imports(org: str | None = typer.Option(None, "--org")):
    """List releases imported into a workspace."""
    settings, store = _settings(), auth.TokenStore()
    organization = _resolve_org(settings, org)
    console.print_json(data={"items": list(_pages(f"/v1/organizations/{organization}/app-imports", settings, store))})


@apps_app.command("install")
def app_install(revision_id: str, robot: str = typer.Option(..., "--robot", help="Target robot name or ID."), org: str | None = typer.Option(None, "--org"), share_id: str | None = typer.Option(None, "--share-id"), config_file: Path | None = typer.Option(None, "--config", help="JSON configuration object."), idempotency_key: str | None = typer.Option(None, "--idempotency-key"), json_output: bool = typer.Option(False, "--json")):  # noqa: B008
    """Install an immutable release on one explicitly selected robot."""
    settings, store = _settings(), auth.TokenStore()
    organization = _resolve_org(settings, org)
    release_id = _app_id(revision_id)
    revision = _api_request("GET", f"/v1/app-revisions/{release_id}", settings, store).json()
    robot_id = _app_target_robot(robot, organization, settings, store)
    body = {
        "revision_id": release_id,
        "share_id": _app_id(share_id) if share_id else None,
        "configuration": _app_config(
            config_file, revision["manifest"]["configuration"]["defaults"]
        ),
    }
    result = _api_request("POST", f"/v1/robots/{robot_id}/app-installations", settings, store,
        headers={"Idempotency-Key": _idempotency_key(idempotency_key)}, json=body, _error_json=json_output).json()
    console.print_json(data=result)


def _app_installation(installation_id: str, settings, store) -> dict:
    return _api_request("GET", f"/v1/app-installations/{_app_id(installation_id)}", settings, store).json()


@apps_app.command("installations")
def app_installations(robot: str = typer.Option(..., "--robot"), org: str | None = typer.Option(None, "--org")):
    """List installed or pending Apps on one robot."""
    settings, store = _settings(), auth.TokenStore()
    organization = _resolve_org(settings, org)
    robot_id = _app_target_robot(robot, organization, settings, store)
    items = list(_pages(f"/v1/robots/{robot_id}/app-installations", settings, store))
    console.print_json(data={"items": items})


@apps_app.command("configure")
def app_configure(installation_id: str, config_file: Path = typer.Option(..., "--config"), idempotency_key: str | None = typer.Option(None, "--idempotency-key"), json_output: bool = typer.Option(False, "--json")):  # noqa: B008
    """Apply a new typed configuration to an installed release."""
    settings, store = _settings(), auth.TokenStore()
    installation = _api_request("GET", f"/v1/app-installations/{_app_id(installation_id)}", settings, store, _error_json=json_output).json()
    binding = installation.get("installed")
    if not binding:
        raise typer.BadParameter("This installation has no confirmed installed release.")
    body = {"expected_configuration_revision_id": binding["configuration_revision_id"], "configuration": _app_config(config_file)}
    result = _api_request("POST", f"/v1/app-installations/{installation['id']}/configure", settings, store,
        headers={"Idempotency-Key": _idempotency_key(idempotency_key)}, json=body, _error_json=json_output).json()
    console.print_json(data=result)


@apps_app.command("update")
def app_update(installation_id: str, revision_id: str, config_file: Path | None = typer.Option(None, "--config"), share_id: str | None = typer.Option(None, "--share-id"), idempotency_key: str | None = typer.Option(None, "--idempotency-key"), json_output: bool = typer.Option(False, "--json")):  # noqa: B008
    """Stage a different immutable release for an installation."""
    settings, store = _settings(), auth.TokenStore()
    installation = _api_request("GET", f"/v1/app-installations/{_app_id(installation_id)}", settings, store, _error_json=json_output).json()
    binding = installation.get("installed")
    if not binding:
        raise typer.BadParameter("This installation has no confirmed installed release.")
    release_id = _app_id(revision_id)
    revision = _api_request("GET", f"/v1/app-revisions/{release_id}", settings, store).json()
    body = {"expected_revision_id": binding["revision_id"], "expected_configuration_revision_id": binding["configuration_revision_id"],
        "revision_id": release_id, "share_id": _app_id(share_id) if share_id else None,
        "configuration": _app_config(config_file, revision["manifest"]["configuration"]["defaults"])}
    if share_id:
        body["share_id"] = _app_id(share_id)
    result = _api_request("POST", f"/v1/app-installations/{installation['id']}/update", settings, store,
        headers={"Idempotency-Key": _idempotency_key(idempotency_key)}, json=body, _error_json=json_output).json()
    console.print_json(data=result)


@apps_app.command("rollback")
def app_rollback(
    installation_id: str,
    revision_id: str = typer.Option(..., "--revision", help="Previously applied release ID."),
    configuration_revision_id: str = typer.Option(..., "--configuration-revision", help="Previously applied configuration revision ID."),
    idempotency_key: str | None = typer.Option(None, "--idempotency-key"),
    json_output: bool = typer.Option(False, "--json"),
) -> None:
    """Restore a previously applied release/configuration binding without starting a run."""
    settings, store = _settings(), auth.TokenStore()
    installation = _api_request(
        "GET", f"/v1/app-installations/{_app_id(installation_id)}", settings, store, _error_json=json_output
    ).json()
    current = installation.get("installed")
    if not current:
        raise typer.BadParameter("This installation has no confirmed installed release.")
    body = {
        "expected_revision_id": current["revision_id"],
        "expected_configuration_revision_id": current["configuration_revision_id"],
        "revision_id": _app_id(revision_id),
        "configuration_revision_id": _app_id(configuration_revision_id),
    }
    result = _api_request(
        "POST",
        f"/v1/app-installations/{installation['id']}/rollback",
        settings,
        store,
        headers={"Idempotency-Key": _idempotency_key(idempotency_key)},
        json=body,
        _error_json=json_output,
    ).json()
    console.print_json(data=result)


@apps_app.command("uninstall")
def app_uninstall(
    installation_id: str,
    yes: bool = typer.Option(False, "--yes", help="Confirm uninstall of this App from this robot."),
    idempotency_key: str | None = typer.Option(None, "--idempotency-key"),
    json_output: bool = typer.Option(False, "--json"),
) -> None:
    """Remove one stopped App installation while retaining its history and files."""
    settings, store = _settings(), auth.TokenStore()
    installation = _api_request(
        "GET", f"/v1/app-installations/{_app_id(installation_id)}", settings, store, _error_json=json_output
    ).json()
    binding = installation.get("installed")
    if not binding or installation.get("removed_at"):
        raise typer.BadParameter("This installation has no confirmed release to remove.")
    app = _api_request("GET", f"/v1/apps/{installation['app_id']}", settings, store).json()
    robot = _api_request("GET", f"/v1/robots/{installation['robot_id']}", settings, store).json()
    revision = _api_request("GET", f"/v1/app-revisions/{binding['revision_id']}", settings, store).json()
    release = revision["manifest"]["release"]
    summary = f"Remove {app['name']} {release} from robot {robot['name']}? Run history and published files will remain available."
    if json_output and not yes:
        raise typer.BadParameter("Automation must pass --yes to confirm uninstall.")
    publish_console.print(summary)
    if not yes and not typer.confirm("Continue with uninstall?", default=False):
        raise typer.Abort()
    body = {
        "expected_revision_id": binding["revision_id"],
        "expected_configuration_revision_id": binding["configuration_revision_id"],
    }
    result = _api_request(
        "POST",
        f"/v1/app-installations/{installation['id']}/uninstall",
        settings,
        store,
        headers={"Idempotency-Key": _idempotency_key(idempotency_key)},
        json=body,
        _error_json=json_output,
    ).json()
    console.print_json(data=result)


@apps_app.command("start")
def app_start(target: str, robot: str | None = typer.Option(None, "--robot", help="Resolve an App ID on this robot; required when targeting an App."), org: str | None = typer.Option(None, "--org"), idempotency_key: str | None = typer.Option(None, "--idempotency-key"), json_output: bool = typer.Option(False, "--json")):
    """Start by installation ID, or resolve an App ID on an explicitly selected robot."""
    settings, store = _settings(), auth.TokenStore()
    if robot:
        organization = _resolve_org(settings, org)
        robot_id = _app_target_robot(robot, organization, settings, store)
        candidates = list(_pages(f"/v1/robots/{robot_id}/app-installations", settings, store))
        try:
            requested = str(uuid.UUID(target))
        except ValueError as error:
            raise typer.BadParameter("Target must be an App ID or installation ID.") from error
        matches = [item for item in candidates if requested in (item.get("id"), item.get("app_id"))]
        if len(matches) != 1:
            raise typer.BadParameter("No unique installation for this App and robot; use an installation ID.")
        installation = matches[0]
    else:
        installation = _app_installation(target, settings, store)
    binding = installation.get("installed")
    if not binding:
        raise typer.BadParameter("This installation has no confirmed installed release.")
    body = {"expected_revision_id": binding["revision_id"], "expected_configuration_revision_id": binding["configuration_revision_id"]}
    result = _api_request("POST", f"/v1/app-installations/{installation['id']}/start", settings, store,
        headers={"Idempotency-Key": _idempotency_key(idempotency_key)}, json=body, _error_json=json_output).json()
    console.print_json(data=result)


@apps_app.command("stop")
def app_stop(installation_id: str, run_id: str = typer.Option(..., "--run", help="Exact run ID to stop."), idempotency_key: str | None = typer.Option(None, "--idempotency-key"), json_output: bool = typer.Option(False, "--json")):
    """Stop one exact run; an older stop can never select a newer run."""
    settings, store = _settings(), auth.TokenStore()
    installation = _app_installation(installation_id, settings, store)
    result = _api_request("POST", f"/v1/app-installations/{installation['id']}/stop", settings, store,
        headers={"Idempotency-Key": _idempotency_key(idempotency_key)}, json={"expected_run_id": _app_id(run_id)}, _error_json=json_output).json()
    console.print_json(data=result)


@apps_app.command("operation")
def app_operation(operation_id: str, wait: bool = typer.Option(False, "--wait"), timeout: int = typer.Option(600, "--timeout", min=1, max=3600)):
    """Inspect an App operation, optionally following it to a terminal state."""
    settings, store = _settings(), auth.TokenStore()
    path = f"/v1/app-operations/{_app_id(operation_id)}"
    deadline = time.monotonic() + timeout
    while True:
        body = _api_request("GET", path, settings, store).json()
        if not wait or body.get("state") in ("succeeded", "failed", "cancelled"):
            console.print_json(data=body)
            if body.get("state") in ("failed", "cancelled"):
                raise typer.Exit(1)
            return
        if time.monotonic() >= deadline:
            typer.echo(f"Timed out waiting for App operation {operation_id}.", err=True)
            raise typer.Exit(1)
        typer.echo(f"App operation {operation_id}: {body.get('state', 'unknown')} / {body.get('phase', 'unknown')}", err=True)
        time.sleep(1)


@apps_app.command("run")
def app_run(run_id: str):
    """Show one App run's durable state and immutable snapshot."""
    body = _api_request("GET", f"/v1/app-runs/{_app_id(run_id)}", _settings(), auth.TokenStore()).json()
    console.print_json(data=body)


@apps_app.command("logs")
def app_logs(run_id: str, follow: bool = typer.Option(False, "--follow", "-f")):
    """Read durable App stdout and stderr, optionally following the run."""
    settings, store = _settings(), auth.TokenStore()
    identifier = _app_id(run_id)
    offsets = {"stdout": 0, "stderr": 0}
    decoders = {name: codecs.getincrementaldecoder("utf-8")("replace") for name in offsets}
    while True:
        run = _api_request("GET", f"/v1/app-runs/{identifier}", settings, store).json()
        sealed = {}
        for stream in ("stdout", "stderr"):
            while True:
                offset = offsets[stream]
                output = _api_request("GET", f"/v1/app-runs/{identifier}/output", settings, store, params={"stream": stream, "offset": offset}).json()
                chunk = base64.b64decode(output["data_base64"], validate=True)
                if output["offset"] != offset or output["next_offset"] != offset + len(chunk):
                    raise typer.Exit(1)
                offsets[stream] = output["next_offset"]
                sealed[stream] = output["sealed"]
                text = decoders[stream].decode(chunk, final=output["sealed"] and not chunk)
                if text:
                    typer.echo(text, nl=False, err=stream == "stderr")
                if not chunk or output["sealed"]:
                    if output.get("truncated"):
                        typer.echo(f"[{stream} output truncated by the runtime limit]", err=True)
                    break
        terminal = run["state"] in ("succeeded", "failed", "cancelled", "interrupted")
        if not follow or terminal:
            if follow and terminal and not all(sealed.values()):
                typer.echo("[App run ended before all output streams reported a seal]", err=True)
            return
        time.sleep(1)


@apps_app.command("artifacts")
def app_artifacts(
    run_id: str | None = typer.Argument(None),
    app_id: str | None = typer.Option(None, "--app", help="List artifacts across an App's installations and runs."),
    org: str | None = typer.Option(None, "--org"),
    robot: str | None = typer.Option(None, "--robot"),
    installation_id: str | None = typer.Option(None, "--installation"),
    selected_run: str | None = typer.Option(None, "--run"),
    path_prefix: str | None = typer.Option(None, "--path-prefix"),
    json_output: bool = typer.Option(False, "--json"),
):
    """List artifacts for one run or across an App with optional target filters."""
    settings, store = _settings(), auth.TokenStore()
    if app_id or robot or installation_id or selected_run or path_prefix:
        if not app_id:
            raise typer.BadParameter("--app is required for filtered, cross-run artifact listing.")
        if run_id:
            if selected_run:
                raise typer.BadParameter("Use either the positional run ID or --run, not both.")
            selected_run = run_id
        organization = _resolve_org(settings, org)
        filters: dict[str, str] = {"organization_id": organization}
        if installation_id:
            filters["installation_id"] = _app_id(installation_id)
        if robot:
            filters["robot_id"] = _app_target_robot(robot, organization, settings, store)
        if selected_run:
            filters["run_id"] = _app_id(selected_run)
        if path_prefix:
            filters["path_prefix"] = path_prefix
        cursor = None
        items = []
        while True:
            params = {**filters, "limit": "100"}
            if cursor:
                params["cursor"] = cursor
            body = _api_request(
                "GET", f"/v1/apps/{_app_id(app_id)}/artifacts", settings, store, params=params
            ).json()
            items.extend(body["items"])
            cursor = body.get("next_cursor")
            if not cursor:
                break
        result = {"items": items, "next_cursor": None}
    else:
        if not run_id:
            raise typer.BadParameter("Provide a run ID, or use --app for cross-run listing.")
        result = _api_request(
            "GET", f"/v1/app-runs/{_app_id(run_id)}/artifacts", settings, store
        ).json()
    if json_output:
        typer.echo(json.dumps(result, ensure_ascii=False))
        return
    table = Table(title="App artifacts")
    table.add_column("Path")
    table.add_column("Size", justify="right")
    table.add_column("Robot")
    table.add_column("Run")
    for item in result["items"]:
        table.add_row(item["path"], str(item["size"]), item.get("robot_id", "—"), item.get("run_id", run_id or "—"))
    console.print(table)


@app_settings_app.command("show")
def app_settings_show(installation_id: str, json_output: bool = typer.Option(False, "--json")):
    """Show selected, applied and pending live settings for an installation."""
    settings, store = _settings(), auth.TokenStore()
    body = _api_request(
        "GET", f"/v1/app-installations/{_app_id(installation_id)}/settings", settings, store
    ).json()
    if json_output:
        typer.echo(json.dumps(body, ensure_ascii=False))
    else:
        console.print_json(data=body)


@app_settings_app.command("set")
def app_settings_set(
    installation_id: str,
    config_file: Path = typer.Option(..., "--file", help="Complete live settings JSON object."),  # noqa: B008
    idempotency_key: str | None = typer.Option(None, "--idempotency-key"),
    json_output: bool = typer.Option(False, "--json"),
):
    """Save complete live settings for next start or request application to the active run."""
    settings, store = _settings(), auth.TokenStore()
    target = _app_id(installation_id)
    current = _api_request("GET", f"/v1/app-installations/{target}/settings", settings, store).json()
    values = _app_config(config_file)
    body = {
        "values": values,
        "expected_selected_revision_id": (current.get("selected_for_next_start") or {}).get("id"),
        "expected_applied_revision_id": (current.get("last_applied") or {}).get("id"),
        "expected_run_id": current.get("active_run_id"),
    }
    result = _api_request(
        "POST",
        f"/v1/app-installations/{target}/settings",
        settings,
        store,
        headers={"Idempotency-Key": _idempotency_key(idempotency_key)},
        json=body,
    ).json()
    if json_output:
        typer.echo(json.dumps(result, ensure_ascii=False))
    elif result.get("state") == "saved_for_next_start":
        console.print("Saved for next start. The App has not confirmed these values yet.")
        console.print_json(data=result)
    else:
        console.print("Settings application pending. Wait for the App to confirm the values.")
        console.print_json(data=result)


@apps_app.command("artifact")
def app_artifact(run_id: str, artifact_id: str, output: Path = typer.Option(..., "--output", "-o")):  # noqa: B008
    """Download one App artifact and verify its declared size and SHA-256."""
    settings, store = _settings(), auth.TokenStore()
    run, artifact = _app_id(run_id), _app_id(artifact_id)
    item = _api_request("GET", f"/v1/app-runs/{run}/artifacts/{artifact}", settings, store).json()
    try:
        if output.exists():
            raise FileExistsError(f"{output} already exists")
        if type(item.get("size")) is not int or item["size"] < 0:
            raise ValueError("API returned an invalid artifact size")
        response = _api_request("GET", f"/v1/app-runs/{run}/artifacts/{artifact}/content", settings, store,
            _timeout=httpx.Timeout(None, connect=15.0, read=300.0))
        data = response.content
        if len(data) != item["size"] or hashlib.sha256(data).hexdigest() != item["sha256"]:
            raise ValueError("Downloaded artifact failed size or SHA-256 verification")
        output.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.NamedTemporaryFile(mode="wb", dir=output.parent, prefix=".ringfour-app-artifact-", delete=False) as target:
            temporary = Path(target.name)
            target.write(data)
            target.flush()
            os.fsync(target.fileno())
        try:
            os.link(temporary, output)
        finally:
            temporary.unlink(missing_ok=True)
    except (OSError, ValueError, httpx.HTTPError) as error:
        console.print(f"[red]Cannot download App artifact: {error}[/red]")
        raise typer.Exit(1) from error
    console.print(f"Saved verified artifact {output}")


@apps_app.command("interaction")
def app_interaction(run_id: str, request_id: str):
    """Show an App-run human interaction and its device-confirmed state."""
    run, request = _app_id(run_id), _app_id(request_id)
    body = _api_request("GET", f"/v1/app-runs/{run}/interactions/{request}", _settings(), auth.TokenStore()).json()
    console.print_json(data=body)


@apps_app.command("respond")
def app_respond(run_id: str, request_id: str, choice: str, idempotency_key: str | None = typer.Option(None, "--idempotency-key")):
    """Submit an App interaction choice; device application remains authoritative."""
    run, request = _app_id(run_id), _app_id(request_id)
    body = _api_request("POST", f"/v1/app-runs/{run}/interactions/{request}/responses", _settings(), auth.TokenStore(),
        headers={"Idempotency-Key": _idempotency_key(idempotency_key)}, json={"chosen_value": choice}).json()
    console.print_json(data=body)
    console.print("[yellow]Response submitted; the device must confirm it before it is applied.[/yellow]")


@apps_app.command("logbook")
def app_logbook(org: str | None = typer.Option(None, "--org"), app_id: str | None = typer.Option(None, "--app"), installation_id: str | None = typer.Option(None, "--installation"), robot: str | None = typer.Option(None, "--robot"), run_id: str | None = typer.Option(None, "--run")):
    """List App and robot events with optional App, installation, robot or run filters."""
    settings, store = _settings(), auth.TokenStore()
    organization = _resolve_org(settings, org)
    filters = {"app_id": app_id, "installation_id": installation_id, "robot_id": robot, "run_id": run_id}
    params = {key: _app_id(value) for key, value in filters.items() if value}
    items = list(_pages(f"/v1/organizations/{organization}/app-logbook?" + urlencode(params), settings, store))
    console.print_json(data={"items": items})


@apps_app.command("build")
def app_build(
    directory: Annotated[Path, typer.Argument()] = Path("."),
    wheels: Annotated[Path | None, typer.Option("--wheels")] = None,
    json_output: Annotated[bool, typer.Option("--json")] = False,
    development: Annotated[bool, typer.Option("--development", help="Build the development Dockerfile target")] = False,
):
    """Build isolated App images from explicit sources and local hash-pinned wheels."""
    _app_result(
        lambda: app_project.build_project(directory.resolve(), wheels, **({"development": True} if development else {})), json_output
    )


@apps_app.command("publish")
def app_publish(
    directory: Annotated[Path, typer.Argument()] = Path("."),
    manifest: Annotated[Path | None, typer.Option("--manifest")] = None,
    org: str | None = typer.Option(None, "--org"),
    visibility: str | None = typer.Option(None, "--visibility", help="Publication visibility: unlisted (private) or listed (public; Ringfour publisher only)."),
    yes: bool = typer.Option(False, "--yes", help="Confirm the displayed publication details."),
    idempotency_key: str | None = typer.Option(None, "--idempotency-key"),
    json_output: bool = typer.Option(False, "--json"),
):
    """Push/verify images, freeze immutable release input, then publish AP-05 metadata."""

    def action():
        settings = load_settings()
        require_auth0(settings)
        organization = org or selected_org(settings)
        if not organization:
            raise ValueError("Select a workspace with org select or --org")
        organization = str(uuid.UUID(organization))
        selected_visibility = visibility or "unlisted"
        if selected_visibility not in ("listed", "unlisted"):
            raise ValueError("visibility must be listed or unlisted")
        if idempotency_key is not None and (
            not 1 <= len(idempotency_key) <= 200
            or any(ord(c) < 32 or ord(c) == 127 for c in idempotency_key)
        ):
            raise ValueError(
                "Idempotency key must contain 1–200 non-control characters"
            )
        store = auth.TokenStore()
        if store.load(settings) is None:
            raise auth.DeviceAuthError("Run ringfour-cli login before publishing")
        release = (
            app_project.verify_prebuilt(manifest)
            if manifest is not None
            else app_project.prepare_publication(directory.resolve())
        )
        body = {"manifest": release, "visibility": selected_visibility}
        visibility_label = "public App Store" if selected_visibility == "listed" else "private to publisher workspace"
        publish_console.print(
            f"Publish [bold]{release['display']['name']}[/bold] "
            f"({release['app_key']} {release['release']}) to workspace {organization} "
            f"as {visibility_label}."
        )
        needs_public_confirmation = selected_visibility == "listed" and not yes
        if needs_public_confirmation and json_output:
            raise typer.BadParameter("Automation must pass --yes to publish a listed release.")
        if needs_public_confirmation and not typer.confirm(
            "Submit this public release?", default=False
        ):
            raise typer.Abort()
        key = idempotency_key or "app:" + app_fingerprint(
            {"api": settings.api_url, "organization": organization, "request": body}
        )
        return _api_request(
            "POST",
            f"/v1/organizations/{organization}/app-revisions",
            settings,
            store,
            headers={"Idempotency-Key": key},
            json=body,
            _error_json=json_output,
            _timeout=90,
        ).json()

    _app_result(action, json_output)


def _pages(path: str, settings, store):
    cursor = None
    while True:
        body = _api_request("GET", path, settings, store, params={"cursor": cursor} if cursor else {}).json()
        yield from body["items"]
        cursor = body.get("next_cursor")
        if not cursor:
            break


@hitl_app.command("list")
def hitl_list(
    org: str | None = typer.Option(None, "--org", help="Organization ID (default: selected org)."),
    state: str | None = typer.Option("pending", "--state", help="pending, answered, timed_out, cancelled, or all."),
    robot: str | None = typer.Option(None, "--robot", help="Filter by robot ID."),
) -> None:
    """List organization human interactions, pending by default."""
    settings, store = _settings(), auth.TokenStore()
    org_id = _resolve_org(settings, org)
    if state == "all":
        state = None
    if state is not None and state not in ("pending", "answered", "timed_out", "cancelled"):
        raise typer.BadParameter("Must be pending, answered, timed_out, cancelled, or all.", param_hint="--state")
    params = {key: value for key, value in {"state": state, "robot_id": robot}.items() if value is not None}
    body = _api_request("GET", f"/v1/organizations/{org_id}/interactions", settings, store, params=params).json()
    table = Table(title="Human interactions")
    for heading in ("Request", "Robot", "State", "Deadline", "Prompt"):
        table.add_column(heading)
    for item in body["items"]:
        table.add_row(item["request_id"], item["robot_id"], item["state"], item["deadline"], item["prompt"])
    console.print(table)
    if body.get("next_cursor"):
        console.print("More interactions are available through cursor pagination.")






@hitl_app.command("show")
def hitl_show(request_id: str) -> None:
    """Show one human interaction and its device-confirmed state."""
    body = _api_request("GET", f"/v1/interactions/{request_id}", _settings(), auth.TokenStore()).json()
    console.print_json(data=body)


@hitl_app.command("respond")
def hitl_respond(
    request_id: str,
    choice: str,
    idempotency_key: str | None = typer.Option(None, "--idempotency-key", help="Stable key for retrying this response."),
) -> None:
    """Submit a listed choice; daemon confirmation remains authoritative."""
    key = idempotency_key or str(uuid.uuid4())
    if not key.strip() or len(key) > 200:
        raise typer.BadParameter("Must be nonblank and at most 200 characters.", param_hint="--idempotency-key")
    body = _api_request("POST", f"/v1/interactions/{request_id}/responses", _settings(), auth.TokenStore(),
        headers={"Idempotency-Key": key}, json={"chosen_value": choice}).json()
    console.print_json(data=body)
    console.print("[yellow]Response submitted; it is not applied until the daemon confirms it.[/yellow]")


@app.command()
def login(
    no_browser: bool = typer.Option(
        False, "--no-browser", help="Do not auto-open the verification URL."
    ),
) -> None:
    """Log in via Auth0 device authorization."""
    settings = _settings()
    store = auth.TokenStore()
    with httpx.Client(timeout=30.0) as client:
        try:
            device = auth.request_device_code(client, settings)
        except auth.DeviceAuthError as error:
            console.print(f"[red]{error}[/red]")
            raise typer.Exit(code=1) from error
        console.print(
            f"Open [bold]{device['verification_uri']}[/bold] and enter code "
            f"[bold]{device['user_code']}[/bold]"
        )
        if device.get("verification_uri_complete"):
            console.print(f"Or open: {device['verification_uri_complete']}")
            if not no_browser:
                try:
                    webbrowser.open(device["verification_uri_complete"])
                except webbrowser.Error:
                    pass
        console.print("Waiting for authorization...")
        try:
            tokens = auth.poll_for_tokens(client, settings, device)
        except auth.DeviceAuthError as error:
            console.print(f"[red]{error}[/red]")
            raise typer.Exit(code=1) from error
    store.save(settings, tokens)
    console.print("[green]Logged in. Credentials stored in the OS credential store.[/green]")


@app.command()
def logout() -> None:
    """Remove stored credentials and the selected organization."""
    settings = _settings()
    store = auth.TokenStore()
    had_credentials = store.clear(settings)
    had_selected_org = selected_org(settings) is not None
    if had_selected_org:
        update_config(settings, selected_org=None)
    if had_credentials and had_selected_org:
        console.print("[green]Logged out and cleared the selected organization.[/green]")
    elif had_credentials:
        console.print("[green]Logged out.[/green]")
    elif had_selected_org:
        console.print("[green]Cleared the selected organization.[/green]")
    else:
        console.print("No stored credentials.")


@app.command()
def whoami() -> None:
    """Show the authenticated user and their memberships."""
    settings = _settings()
    store = auth.TokenStore()
    body = _api_request("GET", "/v1/me", settings, store).json()

    console.print(f"User: {body['user']['id']} (created {body['user']['created_at']})")
    table = Table(title="Memberships")
    table.add_column("Organization")
    table.add_column("Role")
    table.add_column("Since")
    for membership in body["memberships"]:
        table.add_row(
            membership["organization_id"], membership["role"], membership["created_at"]
        )
    console.print(table)


@org_app.command("create")
def org_create(name: str) -> None:
    """Create an organization; you become its owner."""
    settings = _settings()
    store = auth.TokenStore()
    body = _api_request("POST", "/v1/organizations", settings, store, json={"name": name}).json()
    console.print(f"[green]Created organization {body['id']} ({body['name']})[/green]")


@org_app.command("list")
def org_list() -> None:
    """List your organizations."""
    settings = _settings()
    store = auth.TokenStore()
    body = _api_request("GET", "/v1/organizations", settings, store).json()

    selected = selected_org(settings)
    table = Table(title="Organizations")
    table.add_column("")
    table.add_column("ID")
    table.add_column("Name")
    table.add_column("Role")
    for item in body["items"]:
        marker = "*" if item["id"] == selected else ""
        table.add_row(marker, item["id"], item["name"], item["role"])
    console.print(table)
    if body.get("next_cursor"):
        console.print("More organizations available; use the API with cursor pagination.")


@org_app.command("select")
def org_select(org_id: str) -> None:
    """Persist the selected organization for later commands."""
    settings = _settings()
    store = auth.TokenStore()
    body = _api_request("GET", "/v1/organizations", settings, store).json()
    match = next((item for item in body["items"] if item["id"] == org_id), None)
    if match is None:
        console.print(f"[red]You are not a member of organization {org_id}.[/red]")
        raise typer.Exit(code=1)
    update_config(settings, selected_org=org_id)
    console.print(f"[green]Selected organization {match['name']} ({org_id})[/green]")


def _resolve_org(settings, org: str | None) -> str:
    org_id = org or selected_org(settings)
    if not org_id:
        console.print("[red]No organization selected. Use --org or `ringfour-cli org select`.[/red]")
        raise typer.Exit(code=1)
    return org_id


@robot_app.command("create")
def robot_create(
    name: str,
    org: str | None = typer.Option(None, "--org", help="Organization ID (default: selected org)."),
    profile_ref: str | None = typer.Option(None, "--profile-ref", help="External robot profile reference."),
) -> None:
    """Create a robot in an organization (owner-only)."""
    settings = _settings()
    store = auth.TokenStore()
    org_id = _resolve_org(settings, org)
    body = _api_request(
        "POST",
        f"/v1/organizations/{org_id}/robots",
        settings,
        store,
        json={"name": name, "profile_ref": profile_ref},
    ).json()
    console.print(f"[green]Created robot {body['id']} ({body['name']})[/green]")


@robot_app.command("list")
def robot_list(
    org: str | None = typer.Option(None, "--org", help="Organization ID (default: selected org)."),
) -> None:
    """List the organization's robots."""
    settings = _settings()
    store = auth.TokenStore()
    org_id = _resolve_org(settings, org)
    body = _api_request("GET", f"/v1/organizations/{org_id}/robots", settings, store).json()

    table = Table(title="Robots")
    table.add_column("ID")
    table.add_column("Name")
    table.add_column("Enrolled")
    table.add_column("Created")
    for item in body["items"]:
        enrolled = item["enrolled_at"] or "no"
        table.add_row(item["id"], item["name"], enrolled, item["created_at"])
    console.print(table)
    if body.get("next_cursor"):
        console.print("More robots available; use the API with cursor pagination.")


@robot_app.command("show")
def robot_show(robot_id: str) -> None:
    """Show one robot."""
    settings = _settings()
    store = auth.TokenStore()
    body = _api_request("GET", f"/v1/robots/{robot_id}", settings, store).json()
    console.print(f"Robot: {body['id']}")
    console.print(f"  Name:          {body['name']}")
    console.print(f"  Organization:  {body['organization_id']}")
    console.print(f"  Created:       {body['created_at']}")
    console.print(f"  Enrolled:      {body['enrolled_at'] or 'no'}")
    console.print(f"  Profile ref:   {body['profile_ref'] or '-'}")


@robot_app.command("status")
def robot_status(robot_id: str) -> None:
    """Show the robot's observed device status (online/offline, last seen, versions)."""
    settings = _settings()
    store = auth.TokenStore()
    body = _api_request("GET", f"/v1/robots/{robot_id}/status", settings, store).json()
    state = "online" if body["online"] else "offline"
    style = "[green]" if body["online"] else "[red]"
    console.print(f"Robot:            {body['robot_id']}")
    console.print(f"  Status:           {style}{state}[/]")
    console.print(f"  Last seen:        {body['last_seen_at'] or 'never'}")
    console.print(f"  Daemon version:   {body['daemon_version'] or '-'}")
    console.print(f"  Protocol version: {body['protocol_version'] or '-'}")


@robot_app.command("capabilities")
def robot_capabilities(
    robot: str,
    org: str | None = typer.Option(None, "--org"),
    revision_id: str | None = typer.Option(None, "--revision", help="Also evaluate compatibility with this release."),
) -> None:
    """Show the robot's observed App protocol, target and capabilities."""
    settings, store = _settings(), auth.TokenStore()
    organization = _resolve_org(settings, org)
    robot_id = _app_target_robot(robot, organization, settings, store)
    body = _api_request("GET", f"/v1/robots/{robot_id}/app-status", settings, store).json()
    if revision_id:
        body["compatibility"] = _api_request("GET", f"/v1/robots/{robot_id}/app-compatibility", settings, store,
            params={"revision_id": _app_id(revision_id)}).json()
    console.print_json(data=body)


@robot_app.command("enrollment-token")
def robot_enrollment_token(robot_id: str) -> None:
    """Issue a single-use enrollment token (owner-only; shown exactly once)."""
    settings = _settings()
    store = auth.TokenStore()
    body = _api_request(
        "POST", f"/v1/robots/{robot_id}/enrollment-tokens", settings, store
    ).json()
    console.print(f"[bold]{body['token']}[/bold]")
    console.print(
        f"Expires {body['expires_at']}. Single-use and shown exactly once; "
        "pass it to `ringfour-cli daemon enroll`."
    )


@robot_app.command("credentials")
def robot_credentials(robot_id: str) -> None:
    """List a robot's credential metadata (never secrets)."""
    settings = _settings()
    store = auth.TokenStore()
    body = _api_request("GET", f"/v1/robots/{robot_id}/credentials", settings, store).json()

    table = Table(title="Robot credentials")
    table.add_column("ID")
    table.add_column("Created")
    table.add_column("Expires")
    table.add_column("Revoked")
    for item in body["items"]:
        table.add_row(
            item["id"],
            item["created_at"],
            item["expires_at"] or "-",
            item["revoked_at"] or "-",
        )
    console.print(table)


@robot_app.command("rotate")
def robot_rotate(robot_id: str) -> None:
    """Rotate the robot credential (owner-only; new secret shown exactly once)."""
    settings = _settings()
    store = auth.TokenStore()
    body = _api_request("POST", f"/v1/robots/{robot_id}/credentials", settings, store).json()
    credential = body["credential"]
    console.print(f"[green]Created credential {credential['id']}[/green]")
    console.print(f"[bold]{body['secret']}[/bold]")
    console.print(
        "The new secret is shown exactly once. The previous credential keeps "
        "working until its expires_at bounds the rotation overlap."
    )


@robot_app.command("revoke")
def robot_revoke(robot_id: str, credential_id: str) -> None:
    """Revoke one robot credential immediately (owner-only)."""
    settings = _settings()
    store = auth.TokenStore()
    body = _api_request(
        "POST", f"/v1/robots/{robot_id}/credentials/{credential_id}/revoke", settings, store
    ).json()
    console.print(f"[green]Revoked credential {body['id']} at {body['revoked_at']}[/green]")


def _daemon_base_url() -> str:
    return os.environ.get("RINGFOUR_DAEMON_URL", "http://localhost:8081")


@daemon_app.command("enroll")
def daemon_enroll(token: str) -> None:
    """Enroll this device through the local daemon using an enrollment token."""
    with httpx.Client(base_url=_daemon_base_url(), timeout=15.0) as client:
        try:
            response = client.post("/v1/enroll", json={"token": token})
        except httpx.HTTPError:
            console.print("[red]Daemon unreachable. Is the daemon container running?[/red]")
            raise typer.Exit(code=1) from None
    if response.status_code != 200:
        try:
            detail = response.json()["detail"]
        except (ValueError, KeyError):
            detail = f"status {response.status_code}"
        console.print(f"[red]Enrollment failed: {detail}[/red]")
        raise typer.Exit(code=1)
    body = response.json()
    console.print("[green]Device enrolled.[/green]")
    console.print(f"  Robot:         {body['robot_id']}")
    console.print(f"  Organization:  {body['organization_id']}")
    console.print(f"  Credential:    {body['credential_id']}")


@daemon_app.command("identity")
def daemon_identity() -> None:
    """Show the local daemon's enrolled device identity."""
    with httpx.Client(base_url=_daemon_base_url(), timeout=15.0) as client:
        try:
            response = client.get("/v1/identity")
        except httpx.HTTPError:
            console.print("[red]Daemon unreachable. Is the daemon container running?[/red]")
            raise typer.Exit(code=1) from None
    body = response.json()
    if not body["enrolled"]:
        console.print("Device is not enrolled.")
        return
    console.print(f"Robot:         {body['robot_id']}")
    console.print(f"Organization:  {body['organization_id']}")
    console.print(f"Platform:      {body['platform_url']}")
    console.print(f"Credential:    {body['credential_id']}")
    if body.get("error"):
        console.print(f"[red]Error: {body['error']}[/red]")


def _idempotency_key(key: str | None) -> str:
    key = key or str(uuid.uuid4())
    if not key.strip() or len(key) > 180:
        raise typer.BadParameter("Must be nonblank and at most 180 characters.", param_hint="--idempotency-key")
    return key


# App-scoped development uses the normal authenticated API client.
register_app_development(apps_app, _settings, _api_request, app_logs)
