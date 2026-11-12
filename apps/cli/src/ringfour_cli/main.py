import base64
import codecs
import hashlib
import os
import time
import uuid
import webbrowser
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

import httpx
import typer
from rich.console import Console
from rich.table import Table

from . import auth
from .config import (
    ConfigError,
    load_settings,
    require_auth0,
    selected_org,
    update_config,
)
from .script_bundle import build_script_bundle

app = typer.Typer(
    name="ringfour-cli",
    help="Ringfour developer CLI.",
    no_args_is_help=True,
)
org_app = typer.Typer(help="Manage organizations.", no_args_is_help=True)
app.add_typer(org_app, name="org")
robot_app = typer.Typer(help="Manage robots and their credentials.", no_args_is_help=True)
app.add_typer(robot_app, name="robot")
daemon_app = typer.Typer(help="Drive the local device daemon.", no_args_is_help=True)
app.add_typer(daemon_app, name="daemon")
execution_app = typer.Typer(help="Inspect and stop script executions.", no_args_is_help=True)
app.add_typer(execution_app, name="execution")
artifact_app = typer.Typer(help="Download script artifacts.", no_args_is_help=True)
app.add_typer(artifact_app, name="artifact")
console = Console()

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
    tokens = store.load(settings)
    if tokens is None:
        console.print("[red]Not logged in. Run `ringfour-cli login` first.[/red]")
        raise typer.Exit(code=1)
    extra_headers = kwargs.pop("headers", {})
    with httpx.Client(base_url=settings.api_url, timeout=15.0) as client:
        response = client.request(
            method, path, headers={**extra_headers, "Authorization": f"Bearer {tokens['access_token']}"}, **kwargs
        )
        if response.status_code == 401:
            try:
                tokens = auth.refresh_tokens(client, settings, tokens)
            except auth.DeviceAuthError as error:
                console.print(f"[red]{error}[/red]")
                raise typer.Exit(code=1) from error
            store.save(settings, tokens)
            response = client.request(
                method,
                path,
                headers={**extra_headers, "Authorization": f"Bearer {tokens['access_token']}"},
                **kwargs,
            )
    if response.status_code >= 400:
        try:
            error = response.json()["error"]
            console.print(f"[red]{response.status_code} {error['code']}: {error['message']}[/red]")
        except (ValueError, KeyError):
            console.print(f"[red]Request failed with status {response.status_code}.[/red]")
        raise typer.Exit(code=1)
    return response


def _execution_logs(settings, store, execution_id: str, follow: bool) -> dict:
    offsets = {"stdout": 0, "stderr": 0}
    decoders = {name: codecs.getincrementaldecoder("utf-8")("replace") for name in offsets}
    while True:
        execution = _api_request("GET", f"/v1/executions/{execution_id}", settings, store).json()
        eof = {}
        for stream in ("stdout", "stderr"):
            while True:
                output = _api_request("GET", f"/v1/executions/{execution_id}/output", settings, store,
                    params={"stream": stream, "offset": offsets[stream], "limit": 16384}).json()
                data = base64.b64decode(output["data_base64"], validate=True)
                if output["offset"] != offsets[stream] or output["next_offset"] != offsets[stream] + len(data):
                    raise ValueError("Server returned inconsistent output offsets.")
                offsets[stream] = output["next_offset"]
                eof[stream] = output["eof"]
                text = decoders[stream].decode(data, final=output["eof"])
                if text:
                    typer.echo(text, nl=False, err=stream == "stderr")
                if not data or output["eof"]:
                    break
        terminal = execution["state"] in ("succeeded", "failed", "cancelled")
        if not follow or (terminal and all(eof.values())):
            return execution
        time.sleep(1)


@app.command("run")
def run_script(
    script: Path,
    robot: str = typer.Option(..., "--robot", help="Target robot ID."),
    org: str | None = typer.Option(None, "--org", help="Organization ID (default: selected org)."),
    lock: Path | None = typer.Option(None, "--lock", help="Hash-pinned requirements lock file."),  # noqa: B008
    python: str = typer.Option(">=3.13,<3.14", "--python", help="Required Python version."),
    detach: bool = typer.Option(False, "--detach", help="Return after the run is accepted."),
    idempotency_key: str | None = typer.Option(None, "--idempotency-key", help="Reuse this key to retry the same submission."),
) -> None:
    """Upload and run a Python script on an enrolled robot."""
    settings = _settings()
    store = auth.TokenStore()
    org_id = _resolve_org(settings, org)
    key = idempotency_key or str(uuid.uuid4())
    if not key.strip() or len(key) > 180:
        raise typer.BadParameter("Must be nonblank and at most 180 characters.", param_hint="--idempotency-key")
    try:
        bundle = build_script_bundle(script, lock)
    except (OSError, ValueError) as error:
        console.print(f"[red]{error}[/red]")
        raise typer.Exit(1) from error
    typer.echo(f"Submission key: {key}", err=True)
    revision = _api_request("POST", "/v1/script-revisions", settings, store,
        headers={"Idempotency-Key": key + ":revision"}, json=bundle.revision_request(org_id, python)).json()
    try:
        revision_id = str(uuid.UUID(revision["id"]))
        if revision_id != revision["id"] or revision["upload_url"] != f"/v1/script-revisions/{revision_id}/content":
            raise ValueError("Unexpected revision upload URL")
    except (KeyError, ValueError, TypeError, AttributeError) as error:
        console.print("[red]The Platform returned an invalid revision response.[/red]")
        raise typer.Exit(1) from error
    if revision["state"] != "ready":
        _api_request("PUT", revision["upload_url"], settings, store, content=bundle.archive,
            headers={"Content-Type": "application/zip"})
        _api_request("POST", f"/v1/script-revisions/{revision['id']}/finalize", settings, store)
    accepted = _api_request("POST", f"/v1/robots/{robot}/script-runs", settings, store,
        headers={"Idempotency-Key": key + ":run"}, json={"revision_id": revision["id"]}).json()
    execution_id = accepted["execution_id"]
    typer.echo(f"Execution: {execution_id}", err=True)
    if detach:
        return
    try:
        result = _execution_logs(settings, store, execution_id, True)
    except KeyboardInterrupt:
        typer.echo("Interrupted; the execution continues. Use `execution stop` to stop it.", err=True)
        raise typer.Exit(130) from None
    if result["state"] != "succeeded":
        typer.echo(f"Execution {result['state']}", err=True)
        code = result.get("exit_code")
        raise typer.Exit(code if isinstance(code, int) and 1 <= code <= 125 else 1)


@app.command("logs")
def script_logs(execution_id: str, follow: bool = typer.Option(False, "--follow", "-f")) -> None:
    """Read stdout and stderr; optionally follow until the execution finishes."""
    try:
        _execution_logs(_settings(), auth.TokenStore(), execution_id, follow)
    except KeyboardInterrupt:
        raise typer.Exit(130) from None


@execution_app.command("show")
def execution_show(execution_id: str) -> None:
    """Show durable execution state and author."""
    body = _api_request("GET", f"/v1/executions/{execution_id}", _settings(), auth.TokenStore()).json()
    console.print_json(data=body)


@execution_app.command("stop")
def execution_stop(execution_id: str, idempotency_key: str | None = typer.Option(None, "--idempotency-key")) -> None:
    """Request a stop; inspect the execution for its final state."""
    key = idempotency_key or str(uuid.uuid4())
    body = _api_request("POST", f"/v1/executions/{execution_id}/stop", _settings(), auth.TokenStore(),
        headers={"Idempotency-Key": key}).json()
    console.print_json(data=body)


@artifact_app.command("download")
def artifact_download(execution_id: str, path: str, output: Path = typer.Option(..., "--output", "-o")) -> None:  # noqa: B008
    """Download and verify one artifact without overwriting a local file."""
    settings, store = _settings(), auth.TokenStore()
    listing = _api_request("GET", f"/v1/executions/{execution_id}/artifacts", settings, store).json()
    item = next((item for item in listing["artifacts"] if item["path"] == path), None)
    if item is None:
        console.print("[red]Artifact not found.[/red]")
        raise typer.Exit(1)
    data = _api_request("GET", f"/v1/executions/{execution_id}/artifacts/content", settings, store, params={"path": path}).content
    if len(data) != item["size"] or hashlib.sha256(data).hexdigest() != item["sha256"]:
        console.print("[red]Downloaded artifact failed verification.[/red]")
        raise typer.Exit(1)
    try:
        with output.open("xb") as target:
            target.write(data)
    except OSError as error:
        console.print(f"[red]Cannot write artifact: {error}[/red]")
        raise typer.Exit(1) from error
    console.print(f"Saved {output}")


@artifact_app.command("list")
def artifact_list(execution_id: str) -> None:
    """List verified artifacts available for an execution."""
    body = _api_request("GET", f"/v1/executions/{execution_id}/artifacts", _settings(), auth.TokenStore()).json()
    console.print_json(data=body)


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
    """Remove stored credentials from the OS credential store."""
    settings = _settings()
    store = auth.TokenStore()
    if store.clear(settings):
        console.print("[green]Logged out.[/green]")
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


def _fixture_operation(robot_id: str, kind: str, payload: dict) -> None:
    settings = _settings()
    body = _api_request(
        "POST", f"/v1/robots/{robot_id}/operations", settings, auth.TokenStore(),
        json={"kind": kind, "payload_version": 1, "payload": payload},
    ).json()
    console.print_json(data=body)


@robot_app.command("operation")
def robot_operation(robot_id: str, operation_id: str) -> None:
    """Inspect a durable hosted operation and its result."""
    settings = _settings()
    body = _api_request("GET", f"/v1/robots/{robot_id}/operations/{operation_id}", settings, auth.TokenStore()).json()
    console.print_json(data=body)


@robot_app.command("fixture-start")
def fixture_start(robot_id: str, fixture: str) -> None:
    """Start the fixed hello or long diagnostic fixture."""
    if fixture not in ("hello", "long"):
        raise typer.BadParameter("Must be hello or long", param_hint="fixture")
    _fixture_operation(robot_id, "diagnostic.execution.start", {"fixture": fixture})


@robot_app.command("fixture-inspect")
def fixture_inspect(robot_id: str, execution_id: str, stream: str = "stdout", offset: int = 0, limit: int = 4096) -> None:
    """Request an execution snapshot and a base64 output chunk."""
    _fixture_operation(robot_id, "diagnostic.execution.inspect", {
        "execution_id": execution_id, "stream": stream, "offset": offset, "limit": limit,
    })


@robot_app.command("fixture-stop")
def fixture_stop(robot_id: str, execution_id: str) -> None:
    """Request stop for a diagnostic execution."""
    _fixture_operation(robot_id, "diagnostic.execution.stop", {"execution_id": execution_id})


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
