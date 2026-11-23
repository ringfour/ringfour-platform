# Device daemon

The daemon runs on a robot. It owns pairing and robot credentials, durable local
SQLite state, the outbound authenticated Platform connection, and coordination
of mission preparation and execution with the private Runner. Infrastructure
starts the containers; the daemon never controls Docker or mounts its socket.
Its small status page shows pairing information or device and execution status.

## Get started

For the Base101 local simulator and device services, follow the sibling
[`ringfour-virtual` setup](../../../ringfour-virtual/README.md). That Compose
project builds the daemon, Runner and Agent separately from the server stack.
The older full-stack E2E fixture remains at
`tests/integration/compose/docker-compose.yml` for milestone regression scripts:

```sh
docker compose -f tests/integration/compose/docker-compose.yml up -d --build
curl http://localhost:8081/health/ready
```

The local status page is at `http://localhost:8083/`; it is a separate,
unauthenticated, read-only listener, so limit network access appropriately.
Persist the daemon's data volume across restarts. `PLATFORM_API_URL`, `RUNNER_URL`,
`RUNNER_AUTH_TOKEN`, `RUNNER_STAGE_DIR`, `DAEMON_DB_PATH`, and
`DAEMON_CREDENTIAL_PATH` configure its main connections and state. The daemon
and Runner must use the same private token and shared staging volume.
The control listener also accepts execution-scoped mission Logbook entries
when Runner is configured with its private local URL. The daemon persists and
replicates those entries; see [milestone 08 testing](../../docs/human-testing/08.md).

## Develop

```sh
cd apps/daemon
uv sync --frozen
uv run ruff check src tests
uv run pytest -q
```

Start with `src/ringfour_daemon/main.py`, `connection.py`, `pairing.py`,
`scripts.py` and `status_page.py`. SQLite migrations are under `migrations/`.

## App capability inventory (AP-03)

Set `RINGFOUR_AGENT_URI` to the real Agent's `unix:///run/ringfour.sock` or
`ws://agent:10101` endpoint. The Daemon sends only the existing protocol 1
JSON-RPC hello and records the advertised profile/capabilities/instances.
It does not read sensors or infer live measurements from a capability name.

Set `RINGFOUR_ENGINE_OS` and `RINGFOUR_ENGINE_ARCHITECTURE` to the actual
deployment engine's platform (for Docker, obtain these from `docker info`).
The virtual launchers supply these values; they are not inferred from this
container's potentially emulated process architecture. `x86_64` normalizes to
`amd64`, `aarch64` to `arm64`. Unknown well-formed targets remain observable and
will fail matching when no image exists. Optional `RINGFOUR_ENGINE_NATIVE_FACTS`
is a comma-separated list of operator-verified prerequisites. Missing engine
platform configuration disables reporting.

After v1 welcome, the Daemon separately negotiates App protocol 1 with the active
session ID and its persisted deployment namespace. SQLite retains a serial
inventory-report outbox and the last successful Agent observation. A retry sends
the original ID/sequence/body under the new session header. Current inventory
failure is reported as unavailable with explanatory last-successful data.
Defaults: 3-second bounded Agent call and 15-second reporting interval; override
with `RINGFOUR_AGENT_INVENTORY_TIMEOUT_SECONDS` and
`RINGFOUR_APP_INVENTORY_INTERVAL_SECONDS`.

`agent_inventory.check_fresh` is the install/start requirement gate for later
executor slices: it always obtains a new hello and never authorizes an engine
action from cached inventory. App dispatch and the engine adapter remain later
milestones. See `docs/human-testing/AP-03.md` in the Platform repository.

The [architecture](../../docs/architecture.md),
[enrollment](../../docs/human-testing/02.md), and
[mission testing](../../docs/human-testing/06.md) guides explain its boundaries
and end-to-end workflows.

## App engine preview (AP-08)

The optional owner-scoped engine executor is documented in
[engine policy](../../docs/apps-engine.md) and the
[AP-08 operator guide](../../docs/human-testing/AP-08.md). It uses Engine REST
v1.46 over an operator-provided Unix socket and a persistent writer fence;
App readiness is independent of Runner. Hosted App lifecycle dispatch remains
AP-07/AP-09 work. Stack rebuild/recreate is always operator-run.
