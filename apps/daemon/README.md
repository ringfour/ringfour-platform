# Device daemon

The daemon runs on a robot. It owns pairing and robot credentials, durable local
SQLite state, the outbound authenticated Platform connection, and coordination
of mission preparation and execution with the private Runner. Infrastructure
starts the containers; the daemon never controls Docker or mounts its socket.
Its small status page shows pairing information or device and execution status.

## Get started

For a local stack, follow [development setup](../../docs/development.md) and
start Compose from the repository root after configuring Auth0 and the shared
`RUNNER_AUTH_TOKEN`:

```sh
docker compose -f infra/compose/docker-compose.yml up -d --build
curl http://localhost:8081/health/ready
```

The local status page is at `http://localhost:8083/`; it is a separate,
unauthenticated, read-only listener, so limit network access appropriately.
For robot-only testing and deployment see [the robot fixture](../../infra/robot/README.md)
and [Balena notes](../../infra/balena/README.md). Persist the daemon's data
volume across restarts. `PLATFORM_API_URL`, `RUNNER_URL`,
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
