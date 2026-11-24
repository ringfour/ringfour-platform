# Local registry and Joxit deployment follow-up

24 November 2026. The user explicitly selected the official Docker registry and
Joxit UI in the local Compose stack, superseding the tentative Docker Hub path.
Implementation and isolated acceptance pass. No Platform or virtual stack was
started, rebuilt, recreated or reloaded by the agent. Operator deployment remains
pending. [Human instructions](../human-testing/local-registry.md),
[configuration design](../local-registry.md).

## Changes

- Add loopback-only registry port 5000 and UI port 5080 with persistent named
  registry storage and health dependency; both images are version/digest pinned.
- Use Joxit's same-origin Nginx proxy and Docker resolver. Omit REGISTRY_URL; no
  wildcard CORS headers or credentialed cross-origin setup. Relative registry
  upload URLs preserve the client's origin. UI/image deletion remains disabled.
- Add an optional local API endpoint mapping: the image authority remains
  127.0.0.1:5000 while container verification reaches http://app-registry:5000.
  The mapping requires Development, explicit HTTP opt-in, loopback image authority,
  a plain HTTP endpoint without credentials/path/query/fragment. Production
  mappings fail closed before network access. Existing HTTPS policies retain
  their behavior and normal Auth0 is still required.
- Prepare nonsecret local registry settings in the existing ignored `.env`;
  preserve other settings. API installation enablement remains a separate flag.
  No Docker Hub App image/repository was published or credential copied.

Sources were inspected before implementation: Docker's
[official registry image](https://hub.docker.com/_/registry), Joxit's
[configuration/CORS guidance](https://github.com/joxit/docker-registry-ui),
and its upstream `examples/ui-as-proxy/simple.yml`. Joxit 2.6.0 is the verified
release; the configured proxy is upstream's documented approach to CORS.

## Validation

```sh
dotnet test apps/api/tests/Ringfour.Api.Tests --no-restore --verbosity quiet
uv run --project apps/daemon ruff check tests/e2e/local-registry.py
POSTGRES_PASSWORD=compose-render AUTH0_DOMAIN=render.example.invalid AUTH0_WEB_CLIENT_ID=render docker compose -f infra/server/docker-compose.yml config -q
git diff --check
```

API: **157 passed** (148 prior + nine endpoint-routing cases). Real HTTP positive
coverage verifies manifest/config bytes and retains the external image authority.
Negative cases reject production, non-loopback authority, missing HTTP opt-in,
HTTPS mapping and userinfo/path/query/fragment before any network request.
Compose rendering, focused lint, documentation links and whitespace checks pass.

The independent real-process/browser harness:

```sh
uv run --project apps/daemon python tests/e2e/local-registry.py
```

On this host Chromium lacked NSPR/NSS/ALSA libraries. For test execution only,
`apt-get download libnspr4 libnss3 libasound2t64` downloaded 2 MiB of packages and
`dpkg-deb -x` extracted them under `/tmp/ringfour-browser-deps`; no system package
was installed. The actual command used was:

```sh
LD_LIBRARY_PATH=/tmp/ringfour-browser-deps/usr/lib/x86_64-linux-gnu uv run --project apps/daemon python tests/e2e/local-registry.py
```

Passed with the exact Compose image/env configuration:

- Registry `registry:3.1.2@sha256:82f6ff4a7f982960c8aa51617fea3aabc485df6e55cce80d22deb1de36f1bc3c`
- UI `joxit/docker-registry-ui:2.6.0@sha256:bb5956a6b378706f1c3a92c947d0a0c606e280460cd9e37c0198caf46399c839`
- Real pushed/pulled fixture digest
  `sha256:c64c687cbea9300178b30c95835354e34c4e4febc4badfe27102879de0483b5e`
- Actual Chromium rendered the catalog and repository tag page; all registry
  requests used the UI origin's `/v2/` proxy and there were no CORS errors.
- Catalog/manifest/digest responses through Joxit matched the registry.
- Restart of only the test registry retained the image in its fixture volume.
- A real API container on a separate network identity, PostgreSQL and
  Development-only signing published verified metadata using Compose DNS while
  retaining the loopback image reference. The API container had no engine socket.

Test fixtures were independent containers/network/volume and were removed. No live
stack operation or Docker daemon/insecure-registry setting changed. The harness
requires free port 5000: an earlier random loopback port failed the Docker Desktop
push path despite host HTTP succeeding; the actual configured 5000 succeeds for
both localhost and 127.0.0.1, including platform-specific pushes. Browser tests
follow the actual UI-generated tag URL; they test the rendered pages/network
wiring rather than Joxit's ripple-overlay click implementation.

Logs: `/tmp/local-registry-e2e.log`, `/tmp/local-registry-api-tests.log` and
`/tmp/local-registry-routing-tests.log`. Screenshots:
`/tmp/ringfour-local-registry-catalog.png`, `/tmp/ringfour-local-registry-ui.png`.
Live deployment, real Auth0 actor acceptance and live IMU remain separate gates;
this does not mark a hosted/private token provider as accepted.
