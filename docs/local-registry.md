# Local App registry and browser

The local server Compose stack includes Docker's official Distribution registry
and Joxit Docker Registry UI. This replaces the tentative Docker Hub acceptance
path at the user's request. Images remain in the local registry volume.

New local configuration: image references use `127.0.0.1:5000/ringfour/...`, while
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

Operator launch and acceptance commands: [local registry guide](human-testing/local-registry.md).
