# Local Docker registry and Joxit UI

The local Platform Compose now includes pinned official Distribution 3.1.2 and
Joxit Docker Registry UI 2.6.0, with persistent `app-registry-data` storage.
The user selected this local deployment instead of Docker Hub for acceptance.
[Design and sources](../local-registry.md).

## Start the prepared configuration

The registry settings have been prepared in the existing ignored Platform `.env`.
The agent has not started/reloaded/recreated the Platform or virtual stack.
Under the workspace AGENTS.md the operator runs:

```sh
cd /home/cdr/ringfour/ringfour-platform
./up.sh -d app-registry registry-ui api
```

Open **http://localhost:5080**. Registry HTTP is at **http://127.0.0.1:5000**.
Both published ports bind only to loopback. These are local development services;
the API and its normal Auth0 login remain independent of the registry UI.

The local settings are:

```dotenv
ASPNETCORE_ENVIRONMENT=Development
LOCAL_REGISTRY_PORT=5000
REGISTRY_UI_PORT=5080
APPS_REGISTRY_HOST=127.0.0.1:5000
APPS_REGISTRY_REPOSITORY_PREFIX=ringfour
APPS_REGISTRY_ANONYMOUS_PULL=true
APPS_REGISTRY_ALLOW_HTTP=true
APPS_REGISTRY_API_ENDPOINT=http://app-registry:5000
```

The internal API endpoint mapping is accepted only for an explicit local
Development HTTP/loopback policy. Production HTTPS policies keep their prior
behavior; no production authentication bypass is added. Local registry pulls
need no Docker Hub credentials. App installation enablement and Daemon socket
setup are still the separate [AP-07 deployment step](AP-07.md).

## Verify UI and CORS behavior

```sh
curl --fail-with-body -sS http://127.0.0.1:5000/v2/
curl --fail-with-body -sS http://localhost:5080/v2/_catalog
```

Both should succeed; a new catalog is empty. In browser developer tools, the
UI's registry requests must go to its own origin, for example
`http://localhost:5080/v2/_catalog`. Joxit's Nginx proxies those requests internally.

| Caller | Registry route |
|---|---|
| Browser | UI origin on port 5080, `/v2/` |
| Docker build/push/pull and local robot engine | `127.0.0.1:5000` |
| Containerized API and UI's Nginx | Compose DNS `http://app-registry:5000` |

`NGINX_PROXY_PASS_URL` is set, and `REGISTRY_URL` is omitted. This is deliberate:
the browser never needs container DNS or cross-origin registry credentials.
`NGINX_RESOLVER=127.0.0.11` refreshes Docker service resolution. Registry upload
locations are relative, and UI deletion is disabled. There is no wildcard CORS
header configuration to combine incorrectly with credentials.

## Publish a real Robot Check image

From Platform, with the normal CLI Auth0 session and publishing organization:

```sh
uv run --project apps/cli ringfour-cli app init /tmp/local-robot-check \
  --app-key local-robot-check --repository 127.0.0.1:5000/ringfour/robot-check
uv run --project apps/cli ringfour-cli app validate /tmp/local-robot-check --json
uv run --project apps/cli ringfour-cli app build /tmp/local-robot-check --json
uv run --project apps/cli ringfour-cli app publish /tmp/local-robot-check --json
```

The project destination must be new. Pass: publication returns App/revision IDs
and a concrete digest-pinned image. Refresh the registry UI: `ringfour/robot-check`
and its release/architecture tag are visible; inspect the matching content digest.
The API verifies the same registry bytes over internal DNS, while the finalized
manifest retains the loopback authority used by Docker clients.

Continue with [combined Apps acceptance](apps-pivot-acceptance.md), then AP-07's
install/configure/update/rollback checks. Publishing/installing alone creates no
App workload run; hosted start/stop remains AP-09.

Port 5000 is qualified on this Docker Desktop setup. The fixture explicitly uses
that port because an earlier randomly allocated loopback port was reachable by
the host but refused by the engine's push path. No Docker daemon configuration
or global insecure-registry setting was changed.

## Repeat the isolated checks

```sh
dotnet test apps/api/tests/Ringfour.Api.Tests --no-restore
uv run --project apps/daemon python tests/e2e/local-registry.py
```

The second command needs free local port 5000, the existing local alpine:3.20
fixture dependency and Console's Playwright/Chromium browser dependencies. It
creates its own isolated registry/UI/PostgreSQL/API containers and volume, not
Platform/virtual stack services. It pushes/pulls a real digest, renders the actual
Joxit repository/tag page, verifies same-origin requests with no CORS errors,
restarts only its own test registry to check persistence, and publishes through
an actual containerized API using internal DNS. It cleans up its own fixtures.
