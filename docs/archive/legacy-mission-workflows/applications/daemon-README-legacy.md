# Device daemon

The daemon runs on a robot. It owns pairing and robot credentials, durable local
SQLite state, the outbound authenticated Platform connection, and coordination
of legacy mission execution with Runner and opt-in App execution through an
operator-provided engine socket. Infrastructure starts Daemon/Agent; only the
enrolled Daemon manages its owned App workloads. Apps never receive the socket.
Its small status page shows pairing information or device and execution status.

## Get started

For the Base101 local simulator and device services, follow the sibling
[`ringfour-virtual` setup](../../../../../ringfour-virtual/README.md). That Compose
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
replicates those entries; see [milestone 08 testing](../human-testing/08.md).

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

The [architecture](../contracts/architecture-legacy.md),
[enrollment](../human-testing/02.md), and
[mission testing](../human-testing/06.md) guides explain its boundaries
and end-to-end workflows.

## App engine preview (AP-08)

The optional owner-scoped engine executor is documented in
[engine policy](../../../apps-engine.md) and the
[AP-08 operator guide](../../../human-testing/AP-08.md). It uses Engine REST
v1.46 over an operator-provided Unix socket and a persistent writer fence;
App readiness is independent of Runner. AP-07 adds opt-in installation dispatch; hosted starts/stops remain AP-09 work. Stack rebuild/recreate is always operator-run.

Install/configure/update/rollback are described in the [AP-07 guide](../../../human-testing/AP-07.md)
and [installation contract](../../../apps-installation.md). Installation preparation
performs fresh Agent checks and a scoped pull without creating a container/run;
SQLite retains generation, staging, installed binding and unacknowledged reports.

## App runs and runtime (AP-08/09/10)

The opt-in App executor runs independently of Runner readiness. See
[engine ownership](../../../apps-engine.md), [hosted run lifecycle](../../../apps-runs.md)
and [runtime/evidence](../../../apps-runtime.md). The operator supplies the
engine socket/group and network. `RINGFOUR_APP_RUNTIME_URL` is the Daemon HTTP
origin reachable from that App network; the example overlay sets the per-project
Daemon alias. App configuration cannot choose networks, mounts or privileges.

SQLite migrations through 0020 retain enrollment/namespace, run/create/start
intent, ordered reports, output delivery cursors, hashed run credentials and
artifact spool metadata. The private `.runtime-key` next to the database and
`app-artifact-spool/` must persist with daemon-data. Run-scoped engine artifact
volumes are separate from that private data. No App receives the engine socket,
Daemon/Runner secret, human token or registry token. Only run runtime credentials
are passed. Replacements retain the same container; no ambiguous restart replay.

Human checks are batched in [AP-08/09/10](../../../human-testing/AP-08-10.md).
Implementation acceptance uses `tests/e2e/ap08-engine.py`, `ap09-runs.py` and
`ap10-runtime.py` with actual components and disposable test engines. Live stack
reloads remain operator-controlled. Legacy clients keep their Runner paths.
