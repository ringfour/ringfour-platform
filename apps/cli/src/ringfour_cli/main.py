import os
import webbrowser
from collections.abc import Callable
from dataclasses import dataclass

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

app = typer.Typer(
    name="ringfour",
    help="Ringfour developer CLI.",
    no_args_is_help=True,
)
org_app = typer.Typer(help="Manage organizations.", no_args_is_help=True)
app.add_typer(org_app, name="org")
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
        console.print("[red]Not logged in. Run `ringfour login` first.[/red]")
        raise typer.Exit(code=1)
    with httpx.Client(base_url=settings.api_url, timeout=15.0) as client:
        response = client.request(
            method, path, headers={"Authorization": f"Bearer {tokens['access_token']}"}, **kwargs
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
                headers={"Authorization": f"Bearer {tokens['access_token']}"},
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
