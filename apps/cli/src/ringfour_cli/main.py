import base64
import codecs
import hashlib
import os
import secrets
import time
import tomllib
import uuid
import webbrowser
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

import httpx
import typer
from rich.console import Console
from rich.table import Table
from typer.core import TyperGroup

from . import auth
from .config import (
    ConfigError,
    load_settings,
    require_auth0,
    selected_org,
    update_config,
)
from .dev_sync import LocalTransportError, build_snapshot, local_request
from .dev_sync import daemon_url as validate_daemon_url
from .mission_bundle import build_mission_bundle
from .mission_context import (
    CONTEXT_FILE,
    context_api_url,
    mission_directory,
    read_context,
    save_dev_session,
    save_target,
)
from .script_bundle import build_script_bundle


class RingfourGroup(TyperGroup):
    def parse_args(self, ctx, args):
        # Support the requested folder-first form without changing normal commands.
        if len(args) >= 2 and args[1] == "push" and not args[0].startswith("-") and args[0] not in self.commands:
            args = ["mission", "push", args[0], *args[2:]]
        return super().parse_args(ctx, args)


app = typer.Typer(
    cls=RingfourGroup,
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
dev_app = typer.Typer(help="Work on a robot over the local network.", no_args_is_help=True)
app.add_typer(dev_app, name="dev")
mission_app = typer.Typer(help="Publish and operate missions.", no_args_is_help=True)
app.add_typer(mission_app, name="mission")
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


def _mission_key(key: str | None) -> str:
    key = key or str(uuid.uuid4())
    if not key.strip() or len(key) > 180:
        raise typer.BadParameter("Must be nonblank and at most 180 characters.", param_hint="--idempotency-key")
    typer.echo(f"Submission key: {key}", err=True)
    return key


def _pages(path: str, settings, store):
    cursor = None
    while True:
        body = _api_request("GET", path, settings, store, params={"cursor": cursor} if cursor else {}).json()
        yield from body["items"]
        cursor = body.get("next_cursor")
        if not cursor:
            break


def _publish_mission(directory: Path, settings, store, org_id: str, key: str) -> dict:
    try:
        packaged = build_mission_bundle(directory)
    except (OSError, ValueError) as error:
        console.print(f"[red]{error}[/red]")
        raise typer.Exit(1) from error
    manifest, bundle = packaged.manifest, packaged.bundle
    missions = _pages(f"/v1/organizations/{org_id}/missions", settings, store)
    mission = next((item for item in missions if item["name"] == manifest["name"]), None)
    if mission is None:
        mission = _api_request("POST", "/v1/missions", settings, store,
            headers={"Idempotency-Key": key + ":mission"},
            json={"organization_id": org_id, "name": manifest["name"]}).json()
    revision = _api_request("POST", "/v1/script-revisions", settings, store,
        headers={"Idempotency-Key": key + ":source"},
        json=bundle.revision_request(org_id, manifest["python_requirement"])).json()
    try:
        source_id = str(uuid.UUID(revision["id"]))
        if source_id != revision["id"] or revision["upload_url"] != f"/v1/script-revisions/{source_id}/content":
            raise ValueError("Unexpected revision upload URL")
    except (KeyError, ValueError, TypeError, AttributeError) as error:
        console.print("[red]The Platform returned an invalid revision response.[/red]")
        raise typer.Exit(1) from error
    if revision["state"] != "ready":
        _api_request("PUT", revision["upload_url"], settings, store, content=bundle.archive,
            headers={"Content-Type": "application/zip"})
        _api_request("POST", f"/v1/script-revisions/{source_id}/finalize", settings, store)
    # A retry may follow a successful publish whose response was lost. Reuse its
    # original expected version so the server can validate the idempotency key.
    existing = next((item for item in _pages(f"/v1/missions/{mission['id']}/revisions", settings, store)
        if item["script_revision_id"] == source_id and item["manifest"] == manifest), None)
    expected = existing["version"] - 1 if existing else mission["latest_version"]
    return _api_request("POST", f"/v1/missions/{mission['id']}/revisions", settings, store,
        headers={"Idempotency-Key": key + ":publish"},
        json={"script_revision_id": source_id, "expected_version": expected, "manifest": manifest}).json()


def _target_robot(settings, store, org_id: str, robot: str | None) -> tuple[str, str | None]:
    requested = robot or os.environ.get("RINGFOUR_ROBOT_ID")
    if requested:
        try:
            return str(uuid.UUID(requested)), None
        except ValueError:
            matches = [item for item in _pages(f"/v1/organizations/{org_id}/robots", settings, store) if item.get("name") == requested or item.get("id") == requested]
            if len(matches) != 1:
                console.print("[red]Robot name not found or ambiguous in this organization; use its ID.[/red]")
                raise typer.Exit(1)
            return matches[0]["id"], matches[0].get("name")
    online = []
    for item in _pages(f"/v1/organizations/{org_id}/robots", settings, store):
        if item.get("enrolled_at") and _api_request("GET", f"/v1/robots/{item['id']}/status", settings, store).json()["online"]:
            online.append(item)
    if len(online) != 1:
        console.print("[red]Specify --robot or RINGFOUR_ROBOT_ID; exactly one online enrolled robot is required for automatic selection.[/red]")
        raise typer.Exit(1)
    return online[0]["id"], online[0].get("name")


def _context_target(settings, store, org: str | None, robot: str | None) -> dict:
    try:
        api_url = context_api_url(settings.api_url)
        directory = mission_directory(Path.cwd())
        document = read_context(directory)
        manifest = tomllib.loads((directory / "mission.toml").read_text(encoding="utf-8"))
        name = manifest.get("name")
        if not isinstance(name, str) or not name.strip():
            raise ValueError("Mission manifest has no valid name.")
    except (OSError, UnicodeError, ValueError, tomllib.TOMLDecodeError) as error:
        console.print(f"[red]{error}[/red]")
        raise typer.Exit(1) from error
    org_id = _resolve_org(settings, org)
    targets = [item for item in document["targets"] if item["api_url"] == api_url and item["organization_id"] == org_id and item["mission_name"] == name]
    if robot:
        targets = [item for item in targets if item["robot_id"] == robot or item["robot_name"] == robot]
    if not targets:
        missions = [item for item in _pages(f"/v1/organizations/{org_id}/missions", settings, store) if item["name"] == name]
        if len(missions) == 1:
            mission = missions[0]
            revisions = {item["id"] for item in _pages(f"/v1/missions/{mission['id']}/revisions", settings, store)}
            robots = {item["id"]: item for item in _pages(f"/v1/organizations/{org_id}/robots", settings, store)}
            for item in _pages(f"/v1/organizations/{org_id}/deployments", settings, store):
                revision_id = item.get("candidate_revision_id") or item.get("selected_revision_id")
                device = robots.get(item["robot_id"], {})
                if revision_id in revisions and (not robot or robot in (item["robot_id"], device.get("name"))):
                    targets.append({"api_url": api_url, "organization_id": org_id, "mission_name": name,
                        "mission_id": mission["id"], "robot_id": item["robot_id"], "robot_name": device.get("name"),
                        "deployment_id": item["id"], "revision_id": revision_id})
            if len(targets) == 1:
                try:
                    save_target(directory, targets[0])
                    typer.echo(f"Recovered {CONTEXT_FILE} from the Platform.", err=True)
                except (OSError, ValueError) as error:
                    typer.echo(f"Could not save {CONTEXT_FILE}: {error}", err=True)
    if len(targets) != 1:
        console.print(f"[red]{'No matching mission context or deployment. Push this mission first.' if not targets else 'Multiple matching robots; specify --robot by name or ID.'}[/red]")
        raise typer.Exit(1)
    target = targets[0]
    typer.echo(f"Mission {name} → robot {target['robot_name'] or target['robot_id']} ({target['robot_id']})", err=True)
    return target


def _context_deployment(settings, store, org: str | None, robot: str | None) -> tuple[dict, dict]:
    target = _context_target(settings, store, org, robot)
    deployment = _api_request("GET", f"/v1/deployments/{target['deployment_id']}", settings, store).json()
    if deployment.get("robot_id") != target["robot_id"]:
        console.print("[red]Stored deployment no longer belongs to this robot; push again.[/red]")
        raise typer.Exit(1)
    if deployment.get("candidate_revision_id") is not None or deployment.get("selected_revision_id") != target["revision_id"]:
        console.print("[red]The last pushed revision is not selected or is still preparing; check deployment status before acting.[/red]")
        raise typer.Exit(1)
    return target, deployment


@mission_app.command("publish")
def mission_publish(directory: Path, org: str | None = typer.Option(None, "--org"),
    idempotency_key: str | None = typer.Option(None, "--idempotency-key")) -> None:
    """Upload and publish an immutable mission revision."""
    settings, store = _settings(), auth.TokenStore()
    body = _publish_mission(directory, settings, store, _resolve_org(settings, org), _mission_key(idempotency_key))
    console.print_json(data=body)


@app.command("deploy")
def deploy_mission(revision_id: str, robot: str = typer.Option(..., "--robot"),
    idempotency_key: str | None = typer.Option(None, "--idempotency-key")) -> None:
    """Prepare and select a published mission revision on a robot."""
    body = _api_request("POST", f"/v1/robots/{robot}/deployments", _settings(), auth.TokenStore(),
        headers={"Idempotency-Key": _mission_key(idempotency_key)}, json={"mission_revision_id": revision_id}).json()
    console.print_json(data=body)


@mission_app.command("push")
def mission_push(directory: Path, robot: str | None = typer.Option(None, "--robot"),
    org: str | None = typer.Option(None, "--org"),
    idempotency_key: str | None = typer.Option(None, "--idempotency-key"),
    start: bool = typer.Option(False, "--start", help="Wait for preparation, then start the revision."),
    follow: bool = typer.Option(False, "--follow", "-f", help="Follow output after --start.")) -> None:
    """Publish and deploy a mission; optionally start and follow it."""
    if follow and not start:
        raise typer.BadParameter("--follow requires --start.")
    settings, store = _settings(), auth.TokenStore()
    org_id, key = _resolve_org(settings, org), _mission_key(idempotency_key)
    try:
        api_url = context_api_url(settings.api_url)
        document = read_context(directory)
        project = tomllib.loads((directory / "mission.toml").read_text(encoding="utf-8"))
        remembered = [item for item in document["targets"] if item["api_url"] == api_url and item["organization_id"] == org_id and item["mission_name"] == project.get("name")]
        if not robot and not os.environ.get("RINGFOUR_ROBOT_ID"):
            if len(remembered) > 1:
                raise ValueError("Multiple remembered robots; specify --robot by name or ID.")
            if remembered:
                robot = remembered[0]["robot_id"]
    except (ValueError, OSError, UnicodeError, tomllib.TOMLDecodeError) as error:
        console.print(f"[red]{error}[/red]")
        raise typer.Exit(1) from error
    target, robot_name = _target_robot(settings, store, org_id, robot)
    revision = _publish_mission(directory, settings, store, org_id, key)
    body = _api_request("POST", f"/v1/robots/{target}/deployments", settings, store,
        headers={"Idempotency-Key": key + ":deploy"}, json={"mission_revision_id": revision["id"]}).json()
    try:
        mission = tomllib.loads((directory / "mission.toml").read_text(encoding="utf-8"))
        save_target(directory, {"api_url": api_url, "organization_id": org_id,
            "mission_name": mission["name"], "mission_id": revision["mission_id"], "robot_id": target,
            "robot_name": robot_name, "deployment_id": body["deployment_id"], "revision_id": revision["id"]})
    except (OSError, ValueError, KeyError, UnicodeError, tomllib.TOMLDecodeError) as error:
        typer.echo(f"Deployment accepted, but could not save {CONTEXT_FILE}: {error}", err=True)
    console.print_json(data={"mission_revision_id": revision["id"], **body})
    if start:
        typer.echo(f"Waiting for preparation on {robot_name or target}…", err=True)
        deadline = time.monotonic() + 600
        while time.monotonic() < deadline:
            current = _api_request("GET", f"/v1/deployments/{body['deployment_id']}", settings, store).json()
            if current.get("candidate_revision_id") is None:
                if current.get("last_error") or current.get("selected_revision_id") != revision["id"] or current.get("last_operation_id") != body["operation_id"]:
                    error = current.get("last_error") or {}
                    console.print(f"[red]Preparation did not select this revision: {error.get('message', 'check deployment status')}[/red]")
                    raise typer.Exit(1)
                break
            time.sleep(1)
        else:
            console.print("[red]Preparation is still pending; use status to check it. The mission was not started.[/red]")
            raise typer.Exit(1)
        accepted = _api_request("POST", f"/v1/deployments/{body['deployment_id']}/start", settings, store,
            headers={"Idempotency-Key": key + ":start"}).json()
        console.print_json(data=accepted)
        if follow:
            execution_id = accepted["execution_id"]
            typer.echo(f"Waiting for execution {execution_id}…", err=True)
            deadline = time.monotonic() + 600
            while time.monotonic() < deadline:
                current = _api_request("GET", f"/v1/deployments/{body['deployment_id']}", settings, store).json()
                if current.get("last_execution_id") == execution_id:
                    break
                if current.get("last_error"):
                    console.print(f"[red]Start failed: {current['last_error']['message']}[/red]")
                    raise typer.Exit(1)
                time.sleep(1)
            else:
                console.print("[red]Start was accepted but execution is not available yet; check status.[/red]")
                raise typer.Exit(1)
            try:
                result = _execution_logs(settings, store, execution_id, True)
            except KeyboardInterrupt:
                typer.echo("Interrupted; the execution continues.", err=True)
                raise typer.Exit(130) from None
            if result["state"] != "succeeded":
                typer.echo(f"Execution {result['state']}", err=True)
                code = result.get("exit_code")
                raise typer.Exit(code if isinstance(code, int) and 1 <= code <= 125 else 1)


def _mission_action(deployment_id: str | None, action: str, key: str | None, robot: str | None, org: str | None) -> None:
    settings, store = _settings(), auth.TokenStore()
    if deployment_id is None:
        target, _ = _context_deployment(settings, store, org, robot)
        deployment_id = target["deployment_id"]
    elif robot or org:
        raise typer.BadParameter("--robot and --org are only used when no deployment ID is supplied.")
    body = _api_request("POST", f"/v1/deployments/{deployment_id}/{action}", settings, store,
        headers={"Idempotency-Key": _mission_key(key)}).json()
    console.print_json(data=body)


@mission_app.command("start")
def mission_start(deployment_id: str | None = typer.Argument(None), robot: str | None = typer.Option(None, "--robot"), org: str | None = typer.Option(None, "--org"), idempotency_key: str | None = typer.Option(None, "--idempotency-key")) -> None:
    """Start the selected revision."""
    _mission_action(deployment_id, "start", idempotency_key, robot, org)


@mission_app.command("stop")
def mission_stop(deployment_id: str | None = typer.Argument(None), robot: str | None = typer.Option(None, "--robot"), org: str | None = typer.Option(None, "--org"), idempotency_key: str | None = typer.Option(None, "--idempotency-key")) -> None:
    """Stop the selected mission."""
    _mission_action(deployment_id, "stop", idempotency_key, robot, org)


@mission_app.command("restart")
def mission_restart(deployment_id: str | None = typer.Argument(None), robot: str | None = typer.Option(None, "--robot"), org: str | None = typer.Option(None, "--org"), idempotency_key: str | None = typer.Option(None, "--idempotency-key")) -> None:
    """Explicitly restart with a new execution ID."""
    _mission_action(deployment_id, "restart", idempotency_key, robot, org)


@mission_app.command("status")
def mission_status(deployment_id: str | None = typer.Argument(None), robot: str | None = typer.Option(None, "--robot"), org: str | None = typer.Option(None, "--org")) -> None:
    """Inspect the selected revision and lifecycle state."""
    settings, store = _settings(), auth.TokenStore()
    if deployment_id is None:
        target = _context_target(settings, store, org, robot)
        deployment_id = target["deployment_id"]
    elif robot or org:
        raise typer.BadParameter("--robot and --org are only used when no deployment ID is supplied.")
    body = _api_request("GET", f"/v1/deployments/{deployment_id}", settings, store).json()
    console.print_json(data=body)


# Expose the same commands at the top level for working inside a mission.
app.command("start")(mission_start)
app.command("stop")(mission_stop)
app.command("restart")(mission_restart)
app.command("status")(mission_status)


@app.command("logs")
def script_logs(execution_id: str | None = typer.Argument(None), follow: bool = typer.Option(False, "--follow", "-f"), robot: str | None = typer.Option(None, "--robot"), org: str | None = typer.Option(None, "--org")) -> None:
    """Read stdout and stderr; optionally follow until the execution finishes."""
    settings, store = _settings(), auth.TokenStore()
    if execution_id is None:
        target, deployment = _context_deployment(settings, store, org, robot)
        if follow and deployment.get("observed_state") == "starting":
            typer.echo("Waiting for the execution to start…", err=True)
            deadline = time.monotonic() + 60
            while deployment.get("observed_state") == "starting" and time.monotonic() < deadline:
                time.sleep(1)
                _, deployment = _context_deployment(settings, store, org, robot)
        if deployment.get("observed_state") in ("starting", "preparing"):
            console.print("[red]The latest execution is still starting; check status and retry logs.[/red]")
            raise typer.Exit(1)
        execution_id = deployment.get("last_execution_id")
        if not execution_id:
            console.print("[red]This deployment has no execution yet.[/red]")
            raise typer.Exit(1)
        execution = _api_request("GET", f"/v1/executions/{execution_id}", settings, store).json()
        if execution.get("mission_revision_id") != target["revision_id"] or execution.get("deployment_id") != target["deployment_id"]:
            console.print("[red]The latest execution belongs to a different revision; start this mission first.[/red]")
            raise typer.Exit(1)
    elif robot or org:
        raise typer.BadParameter("--robot and --org are only used when no execution ID is supplied.")
    try:
        _execution_logs(settings, store, execution_id, follow)
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


def _dev_project(directory: Path | None = None) -> Path:
    try:
        start = directory or Path.cwd()
        if start.is_symlink():
            raise ValueError("Mission directory must not be a symlink.")
        return mission_directory(start)
    except (OSError, ValueError) as error:
        typer.echo(str(error), err=True)
        raise typer.Exit(1) from error


def _dev_target(settings, org: str | None, robot: str | None, session: str | None, directory: Path):
    org_id = _resolve_org(settings, org)
    try:
        api = context_api_url(settings.api_url)
        document = read_context(directory)
        matches = [item for item in document.get("dev_sessions", []) if item["api_url"] == api and item["organization_id"] == org_id]
        if robot:
            matches = [item for item in matches if robot in (item["robot_id"], item["robot_name"])]
        if session:
            matches = [item for item in matches if item["session_id"] == session]
        if len(matches) != 1:
            raise ValueError("No unique development session in this project; use --robot or run `dev start`.")
        return matches[0]
    except (OSError, ValueError) as error:
        typer.echo(str(error), err=True)
        raise typer.Exit(1) from error


def _wait_dev_operation(settings, store, robot_id: str, operation_id: str, *, attempts: int = 60):
    for _ in range(attempts):
        operation = _api_request("GET", f"/v1/robots/{robot_id}/operations/{operation_id}", settings, store).json()
        if operation.get("state") == "succeeded":
            return
        if operation.get("state") in ("failed", "cancelled"):
            typer.echo("The device could not authorize the local connection.", err=True)
            raise typer.Exit(1)
        time.sleep(1)
    typer.echo("Timed out waiting for the device; try again.", err=True)
    raise typer.Exit(1)


def _local_grant(settings, store, target, daemon_url=None):
    session_id, robot_id = target["session_id"], target["robot_id"]
    chosen_url = daemon_url or target["daemon_url"]
    try:
        if chosen_url:
            chosen_url = validate_daemon_url(chosen_url)
    except ValueError as error:
        typer.echo(str(error), err=True)
        raise typer.Exit(1) from error
    raw_token = secrets.token_bytes(32)
    token = base64.urlsafe_b64encode(raw_token).decode().rstrip("=")
    grant_id = str(uuid.uuid4())
    grant = _api_request("POST", f"/v1/dev-sessions/{session_id}/local-grants", settings, store,
        headers={"Idempotency-Key": str(uuid.uuid4())},
        json={"grant_id": grant_id, "token_sha256": hashlib.sha256(raw_token).hexdigest()}).json()
    try:
        if grant["grant_id"] != grant_id or str(uuid.UUID(grant["operation_id"])) != grant["operation_id"]:
            raise ValueError("Platform returned an invalid grant.")
        chosen_url = validate_daemon_url(chosen_url or grant.get("daemon_url"))
        pin = grant["certificate_sha256"]
    except (KeyError, ValueError, TypeError) as error:
        typer.echo(f"Local connection is not configured: {error}. Use --daemon-url if needed.", err=True)
        raise typer.Exit(1) from error
    _wait_dev_operation(settings, store, robot_id, grant["operation_id"])
    return chosen_url, pin, token


@dev_app.command("start")
def dev_start(
    directory: Path = typer.Argument(Path("."), help="Mission directory."),  # noqa: B008
    robot: str | None = typer.Option(None, "--robot", help="Robot name or ID."),
    org: str | None = typer.Option(None, "--org", help="Organization ID."),
    daemon_url: str | None = typer.Option(None, "--daemon-url", help="HTTPS LAN address if not advertised."),
    ttl: int = typer.Option(3600, "--ttl", min=300, max=28800, help="Session lifetime in seconds."),
    idempotency_key: str | None = typer.Option(None, "--idempotency-key"),
) -> None:
    """Create an empty development workspace on a robot."""
    directory = _dev_project(directory)
    settings, store = _settings(), auth.TokenStore()
    org_id = _resolve_org(settings, org)
    try:
        api = context_api_url(settings.api_url)
        read_context(directory)
        build_snapshot(directory, [], 0)
        if daemon_url:
            daemon_url = validate_daemon_url(daemon_url)
    except (OSError, ValueError) as error:
        typer.echo(str(error), err=True)
        raise typer.Exit(1) from error
    robot_id, robot_name = _target_robot(settings, store, org_id, robot)
    key = _mission_key(idempotency_key)
    result = _api_request("POST", f"/v1/robots/{robot_id}/dev-sessions", settings, store,
        headers={"Idempotency-Key": key}, json={"ttl_seconds": ttl}).json()
    try:
        session = str(uuid.UUID(result["session_id"]))
        if session != result["session_id"]:
            raise ValueError
        target = {"api_url": api, "organization_id": org_id, "robot_id": robot_id, "robot_name": robot_name,
                  "session_id": session, "daemon_url": daemon_url}
        save_dev_session(directory, target)
    except (KeyError, OSError, TypeError, ValueError) as error:
        typer.echo(f"Session was accepted but its context could not be saved: {error}", err=True)
        typer.echo(f"Session ID: {result.get('session_id', 'unknown')}", err=True)
        raise typer.Exit(1) from error
    typer.echo(f"Session {session} created; waiting for the robot…", err=True)
    for _ in range(60):
        status = _api_request("GET", f"/v1/dev-sessions/{session}", settings, store).json()
        if status.get("state") == "ready":
            typer.echo("Development workspace ready. Run `ringfour-cli dev sync` to copy source.")
            return
        if status.get("state") in ("failed", "closed", "expired"):
            typer.echo(f"Session entered {status['state']}.", err=True)
            raise typer.Exit(1)
        time.sleep(1)
    typer.echo("Session creation is still pending; use `ringfour-cli dev status` to check it.", err=True)
    raise typer.Exit(1)


@dev_app.command("sync")
def dev_sync(
    robot: str | None = typer.Option(None, "--robot", help="Robot name or ID."),
    org: str | None = typer.Option(None, "--org", help="Organization ID."),
    session: str | None = typer.Option(None, "--session", help="Remembered session ID."),
    daemon_url: str | None = typer.Option(None, "--daemon-url", help="HTTPS LAN address override."),
) -> None:
    """Sync this project's source directly to the robot over HTTPS."""
    directory = _dev_project()
    settings, store = _settings(), auth.TokenStore()
    target = _dev_target(settings, org, robot, session, directory)
    session_id, robot_id = target["session_id"], target["robot_id"]
    state = _api_request("GET", f"/v1/dev-sessions/{session_id}", settings, store).json()
    if state.get("robot_id") != robot_id or state.get("state") not in ("ready", "running"):
        typer.echo("The remembered development session is not ready on this robot.", err=True)
        raise typer.Exit(1)
    chosen_url, pin, token = _local_grant(settings, store, target, daemon_url)
    path = f"/v1/local/dev-sessions/{session_id}/source"
    try:
        status, current = local_request(chosen_url, pin, token, "GET", path)
        if status != 200 or not isinstance(current, dict) or set(current) != {"generation", "paths", "archive_sha256"}:
            raise ValueError("Could not read source state from the local daemon.")
        remote_hash = current["archive_sha256"]
        if ((current["generation"] == 0 and remote_hash is not None) or
                (current["generation"] != 0 and (not isinstance(remote_hash, str) or len(remote_hash) != 64 or any(char not in "0123456789abcdef" for char in remote_hash)))):
            raise ValueError("Daemon returned invalid source state.")
        body = build_snapshot(directory, current["paths"], current["generation"])
        if remote_hash == body["archive_sha256"]:
            typer.echo(f"Source is already up to date (generation {current['generation']}).")
            return
        key = str(uuid.uuid4())
        deadline = time.monotonic() + 45
        while time.monotonic() < deadline:
            try:
                status, response = local_request(chosen_url, pin, token, "PUT", path, body=body, key=key)
                if status == 503 and response.get("error", {}).get("code") == "sync_pending":
                    time.sleep(1)
                    continue
                break
            except LocalTransportError:
                time.sleep(1)
        else:
            raise ValueError("Local sync is still pending; run `dev sync` again to reconcile it.")
        if status != 200:
            code = response.get("error", {}).get("code", "local_error") if isinstance(response, dict) else "local_error"
            raise ValueError(f"Local sync failed: {code}.")
        if (not isinstance(response, dict) or response.get("session_id") != session_id or
                response.get("generation") != body["base_generation"] + 1 or response.get("archive_sha256") != body["archive_sha256"]):
            raise ValueError("Daemon returned an inconsistent sync result.")
        typer.echo(f"Source synced to the robot (generation {response['generation']}).")
    except (OSError, ValueError, KeyError, TypeError) as error:
        typer.echo(str(error), err=True)
        raise typer.Exit(1) from error


@dev_app.command("status")
def dev_status(robot: str | None = typer.Option(None, "--robot"), org: str | None = typer.Option(None, "--org")) -> None:
    """Show the remembered development session's Platform state."""
    directory = _dev_project()
    settings, store = _settings(), auth.TokenStore()
    target = _dev_target(settings, org, robot, None, directory)
    console.print_json(data=_api_request("GET", f"/v1/dev-sessions/{target['session_id']}", settings, store).json())


@dev_app.command("stop")
def dev_stop(robot: str | None = typer.Option(None, "--robot"), org: str | None = typer.Option(None, "--org")) -> None:
    """Close the remembered development session and clean up its workspace."""
    directory = _dev_project()
    settings, store = _settings(), auth.TokenStore()
    target = _dev_target(settings, org, robot, None, directory)
    result = _api_request("DELETE", f"/v1/dev-sessions/{target['session_id']}", settings, store,
        headers={"Idempotency-Key": str(uuid.uuid4())}).json()
    console.print_json(data=result)
    typer.echo("Closure requested; use `dev status` to check cleanup.", err=True)


def _dev_output(settings, store, target, execution_id, connection, *, follow):
    session_id = target["session_id"]
    base = f"/v1/local/dev-sessions/{session_id}/executions/{execution_id}"
    offsets = {"stdout": 0, "stderr": 0}
    decoders = {stream: codecs.getincrementaldecoder("utf-8")("replace") for stream in offsets}
    deadline = time.monotonic() + 60
    warned, ended = set(), set()
    while True:
        waiting = False
        more = False
        for stream, offset in offsets.items():
            if stream in ended:
                continue
            url, pin, token = connection
            status, body = local_request(url, pin, token, "GET", f"{base}/output?stream={stream}&offset={offset}&limit=16384")
            if status == 401:
                connection = _local_grant(settings, store, target, url)
                waiting = True
                break
            if status in (404, 503) and time.monotonic() < deadline:
                waiting = True
                break
            if status != 200 or not isinstance(body, dict):
                raise ValueError("Could not read execution output from the local daemon.")
            try:
                data = base64.b64decode(body["data_base64"], validate=True)
                if (body["execution_id"] != execution_id or body["stream"] != stream or
                        body["offset"] != offset or body["next_offset"] != offset + len(data) or
                        len(data) > 16384 or type(body["eof"]) is not bool):
                    raise ValueError
            except (KeyError, ValueError, TypeError):
                raise ValueError("Daemon returned invalid execution output.") from None
            offsets[stream] = body["next_offset"]
            more = more or len(data) == 16384
            output = decoders[stream].decode(data, final=body["eof"])
            if output:
                typer.echo(output, nl=False, err=stream == "stderr")
            if body["eof"]:
                ended.add(stream)
            if body.get("truncated") and stream not in warned:
                typer.echo(f"\n{stream} output was truncated on the robot.", err=True)
                warned.add(stream)
        if len(ended) == 2:
            if follow:
                status, execution = local_request(*connection, "GET", base)
                if status != 200 or not isinstance(execution, dict) or execution.get("execution_id") != execution_id:
                    raise ValueError("Could not read the final execution state from the local daemon.")
                if execution.get("state") in ("failed", "cancelled"):
                    raise ValueError(f"Development execution {execution['state']}.")
                if execution.get("state") != "succeeded":
                    raise ValueError("Daemon returned an invalid final execution state.")
            return
        if not follow and not waiting and not more:
            return
        time.sleep(0.5)


@dev_app.command("restart")
def dev_restart(
    robot: str | None = typer.Option(None, "--robot"),
    org: str | None = typer.Option(None, "--org"),
    daemon_url: str | None = typer.Option(None, "--daemon-url"),
    follow: bool = typer.Option(False, "--follow", "-f"),
) -> None:
    """Restart the synced code on the robot; optionally follow its local output."""
    settings, store = _settings(), auth.TokenStore()
    target = _dev_target(settings, org, robot, None, _dev_project())
    session_id = target["session_id"]
    connection = _local_grant(settings, store, target, daemon_url)
    try:
        status, source = local_request(*connection, "GET", f"/v1/local/dev-sessions/{session_id}/source")
        generation = source.get("generation") if isinstance(source, dict) else None
        if status != 200 or type(generation) is not int or not 1 <= generation <= 20:
            raise ValueError("No synced source is available; run `dev sync` first.")
        for _ in range(60):
            state = _api_request("GET", f"/v1/dev-sessions/{session_id}", settings, store).json()
            if state.get("robot_id") != target["robot_id"] or state.get("state") in ("closed", "closing", "expired", "failed"):
                raise ValueError("The development session is unavailable.")
            if state.get("generation") == generation and state.get("state") in ("ready", "running"):
                break
            if type(state.get("generation")) is int and state["generation"] > generation:
                raise ValueError("Local and Platform source generations do not match.")
            time.sleep(1)
        else:
            raise ValueError("Timed out waiting for the Platform to confirm the local source generation.")
        result = _api_request("POST", f"/v1/dev-sessions/{session_id}/restart", settings, store,
            headers={"Idempotency-Key": str(uuid.uuid4())}, json={"generation": generation}).json()
        execution_id = result["execution_id"]
        if str(uuid.UUID(execution_id)) != execution_id or result.get("session_id") != session_id:
            raise ValueError("Platform returned an invalid execution.")
        typer.echo(f"Development execution {execution_id} requested.", err=True)
        if follow:
            _dev_output(settings, store, target, execution_id, connection, follow=True)
    except (OSError, ValueError, KeyError, TypeError) as error:
        typer.echo(str(error), err=True)
        raise typer.Exit(1) from error
    except KeyboardInterrupt:
        typer.echo("Stopped following output; execution continues on the robot.", err=True)
        raise typer.Exit(130) from None


@dev_app.command("logs")
def dev_logs(
    robot: str | None = typer.Option(None, "--robot"),
    org: str | None = typer.Option(None, "--org"),
    daemon_url: str | None = typer.Option(None, "--daemon-url"),
    follow: bool = typer.Option(False, "--follow", "-f"),
) -> None:
    """Read the latest development execution's output directly from the robot."""
    settings, store = _settings(), auth.TokenStore()
    target = _dev_target(settings, org, robot, None, _dev_project())
    state = _api_request("GET", f"/v1/dev-sessions/{target['session_id']}", settings, store).json()
    try:
        execution_id = state["last_execution_id"]
        if str(uuid.UUID(execution_id)) != execution_id or state.get("robot_id") != target["robot_id"]:
            raise ValueError("No development execution is available.")
        connection = _local_grant(settings, store, target, daemon_url)
        _dev_output(settings, store, target, execution_id, connection, follow=follow)
    except (OSError, ValueError, KeyError, TypeError) as error:
        typer.echo(str(error), err=True)
        raise typer.Exit(1) from error
    except KeyboardInterrupt:
        typer.echo("Stopped following output; execution continues on the robot.", err=True)
        raise typer.Exit(130) from None
