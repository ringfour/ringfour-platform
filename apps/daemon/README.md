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
The [architecture](../../docs/architecture.md),
[enrollment](../../docs/human-testing/02.md), and
[mission testing](../../docs/human-testing/06.md) guides explain its boundaries
and end-to-end workflows.
