# Local App registry and browser

The local server Compose stack includes Docker's official Distribution registry
and Joxit Docker Registry UI. Images remain in the local registry volume.

Local image references use `127.0.0.1:5000/ringfour/...`, while
the API verifies content over Compose DNS at `http://app-registry:5000`. Optional
`Apps:Registries:0:ApiEndpoint` supplies this internal route without changing the
immutable image authority returned to Docker/Daemon clients. This mapping is
accepted only in Development, with AllowHttp enabled and a loopback image
registry authority. It cannot redirect hosted/production verification. Ordinary
HTTPS registry policies retain their existing behavior and Auth0 remains required.

The local `.env` sets Development plus this explicit local policy; production
configuration keeps HTTPS and must omit the internal endpoint mapping. The
registry/UI are loopback-only, unauthenticated local development services. Robot
engines on another host need a separately configured reachable registry.

Joxit uses `NGINX_PROXY_PASS_URL=http://app-registry:5000` and Docker DNS resolver
`127.0.0.11`. REGISTRY_URL is deliberately omitted: the browser uses its own UI
origin and `/v2/`, not container DNS or a cross-origin registry port. No wildcard
CORS or credentialed cross-origin workaround is required. Relative upload URLs
keep requests on the caller's authority. UI deletion is disabled by default.

Sources: [official registry](https://hub.docker.com/_/registry),
[Joxit configuration and proxy/CORS guidance](https://github.com/joxit/docker-registry-ui#available-options).

## Operator setup

Configure the Platform checkout’s ignored `.env`. The operator runs the following command from that checkout.

```sh
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
need no Docker Hub credentials. See [App installation](apps-installation.md) and [engine configuration](apps-engine.md) for Daemon setup.

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
uv run --project apps/cli ringfour-cli app bundle fetch /tmp/local-robot-check --json
uv run --project apps/cli ringfour-cli app build /tmp/local-robot-check --json
uv run --project apps/cli ringfour-cli app publish /tmp/local-robot-check --json
```

Use a new project destination. Successful publication returns App/revision IDs
and a concrete digest-pinned image. Refresh the registry UI: `ringfour/robot-check`
and its release/architecture tag are visible; inspect the matching content digest.
The API verifies the same registry bytes over internal DNS, while the finalized
manifest retains the loopback authority used by Docker clients.

Publishing and installing an App are separate from starting it. See [hosted runs](apps-runs.md).
