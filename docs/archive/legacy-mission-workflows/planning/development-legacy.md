# Development commands

Established in milestone 00. Runtime services always run in Docker Compose; host tooling
(Node, uv) is used only for lint/typecheck/component tests. The .NET SDK runs in Docker
via `scripts/dotnet.sh`; no host .NET installation is required or supported.

## Executables

Two Python executables exist, one per app — the names say which process you
are dealing with:

- `ringfour-cli` (`apps/cli`, run on your machine via `uv run` from `apps/cli/`)
  is the developer CLI: login, organizations, robots, credentials, and the
  `daemon enroll` / `daemon identity` subcommands that talk to the local
  daemon over HTTP.
- `ringfour-daemon` (`apps/daemon`) is the device daemon process itself. In
  Compose it is the container's `CMD`; locally you can run
  `uv run ringfour-daemon` from `apps/daemon/` for development. It is not a
  subcommand of the CLI.

## Prerequisites

- Docker (with Compose v2+)
- uv (Python apps: daemon, runner, cli) — uv manages its own Python 3.13
- Node.js (contracts tests, console component tests)
- Docker must be running for API component tests (Testcontainers)

## Commands

| Command | Purpose |
|---|---|
| `npm run setup` | Install all host-side dependencies (contracts, console, uv sync, dotnet restore in Docker) |
| `npm run lint` | Lint every app (dotnet format, ruff, biome) |
| `npm run typecheck` | Type checks (dotnet build, tsc) |
| `npm test` | Component tests per app (xUnit+Testcontainers, pytest, vitest, contracts) |
| `npm run test:e2e` | Milestone 00 integrated acceptance against the real Compose stack |
| `npm run test:e2e:01` | Milestone 01 identity acceptance (requires a completed `ringfour-cli login`) |
| `npm run test:e2e:02` | Milestone 02 robot enrollment acceptance (requires `ringfour-cli login`; resets the daemon volume) |
| `npm run test:e2e:pairing` | Device pairing through the Console proxy and isolated daemon (requires owner login and selected org) |
| `npm run test:e2e:03` | Milestone 03 connectivity acceptance (requires `ringfour-cli login`; resets the daemon volume; ~4 min, includes two 45 s offline-threshold waits) |
| `npm run test:e2e:04` | Local Runner acceptance with isolated daemon/Runner containers and volumes; requires an owner login and selected organization |
| `npm run integration:up` | Start the legacy full-stack E2E fixture |
| `npm run integration:down` | Stop that fixture (volumes kept) |
| `npm run integration:clean` | Stop it and delete its volumes |
| `npm run integration:logs` | Follow its logs |

## Start the server stack

Run `./up.sh` from the Platform repository root (or call it by absolute path).
It loads `.auth.env` and then root `.env` when present, and starts PostgreSQL,
the Platform API, and the internal Console from `infra/server/docker-compose.yml`.
The root `.env` takes precedence. Set `AUTH0_DOMAIN` and
`AUTH0_WEB_CLIENT_ID` before starting. If
`POSTGRES_PASSWORD` is unset, the script creates a persistent random password
in the ignored `.env.server` file. Local filesystem artifact storage is the
default; set `STORAGE_BACKEND=s3` plus the `STORAGE_S3_*` settings to use an
S3-compatible store. Existing server Compose volumes are preserved. Pass
additional Compose `up` options or service names after the script name, for
example `./up.sh api`.
Open the Console at `http://localhost:5173/` (or the configured
`CONSOLE_ORIGIN`).

For the Base101 virtual device, initialize and start the pinned simulator in
`../ringfour-virtual/dependencies/base101` (see its README), then run
`../ringfour-virtual/up.sh`. That project builds Agent with SDK source and
daemon and Runner from their own directories. It joins the simulator's ROS
network and connects to the server over the device protocol; it has no shared
source, package, database, or Docker network with the server.

`tests/integration/compose/docker-compose.yml` is a legacy, combined test
fixture used by older acceptance scripts. It is not deployment infrastructure
or the recommended development stack.

## Dependency updates and quality gate

`npm run setup` uses the checked-in npm and uv lockfiles; daemon and Runner
images install with `uv sync --frozen --no-dev`. Review dependency updates
monthly and promptly for security fixes: update lockfiles deliberately, review
major-version changes, and run the quality gate and applicable integration
gates before merging. Unconstrained manifest ranges do not authorize deployment
from a freshly resolved dependency graph.

Run `bash scripts/quality.sh` for contract tests, lint, type checks and each
component suite. For the automated local integration gates, use
`bash scripts/quality.sh --integration` after configuring Auth0, setting
`RUNNER_AUTH_TOKEN` and signing in as an owner with an organization selected.
The legacy 00/02/03 scripts delete
Compose volumes, including local database and daemon state; run that option only
on a disposable stack. It also runs the isolated pre-ack and credential recovery
scenario. Live Auth0 sign-in and any physical-device gates remain separately
recorded manual prerequisites; the script does not claim them as passed.

## Migrations

- API (EF Core): `bash scripts/dotnet.sh ef migrations add <Name> --project src/Ringfour.Api`.
  Migrations apply automatically at API startup (`Database:MigrateOnStartup`).
- Daemon (SQLite): add `NNNN_name.sql` to `apps/daemon/src/ringfour_daemon/migrations/`.
  Applied at daemon startup; already-applied versions are skipped.

## Environment

Copy `.env.example` to `.env` to override defaults (all values are dev-only, no secrets).
CLI configuration: `RINGFOUR_API_URL`, `RINGFOUR_DAEMON_URL`.

## Auth0 (milestone 01)

`.auth.env` (gitignored, holds tenant identifiers) provides `AUTH0_DOMAIN`,
`AUTH0_AUDIENCE`, `AUTH0_CLI_CLIENT_ID`, `AUTH0_WEB_CLIENT_ID`. The legacy test
fixture reads it through the ignored symlink `tests/integration/compose/.env`;
the server wrapper loads it directly. The API gets `Auth0__Domain`/
`Auth0__Audience` and the Console `VITE_AUTH0_*` from it.

The CLI reads `RINGFOUR_AUTH0_DOMAIN`, `RINGFOUR_AUTH0_CLIENT_ID`,
`RINGFOUR_AUTH0_AUDIENCE` (plus `RINGFOUR_API_URL`) from the environment or
`~/.config/ringfour/config.json`; tokens live in the OS credential store (keyring),
the selected org in the config file. See `docs/human-testing/01.md` for the full
manual flow and `docs/evidence/01.md` for the required tenant settings.

## Robot enrollment (milestone 02)

API configuration: `Enrollment__TokenTtlMinutes` (default 15) and
`Enrollment__RotationOverlapSeconds` (default 300). The daemon reads
`PLATFORM_API_URL` (compose sets `http://api:8080`) and stores the credential
secret at `DAEMON_CREDENTIAL_PATH` (default `/data/robot-credential`, mode 0600);
the durable binding lives in SQLite table `device_identity` (migration 0002).

CLI commands: `ringfour-cli robot create|list|show|status|enrollment-token|credentials|rotate|revoke`
(owner-only administration; members can read) and `ringfour-cli daemon enroll|identity`
(local daemon, no login needed). See `docs/human-testing/02.md` for the manual
flow and `docs/evidence/02.md` for acceptance results.

## Runner diagnostic execution (milestone 04)

Before starting or rebuilding the local Compose stack, set the same random
`RUNNER_AUTH_TOKEN` for the daemon and Runner. Compose passes it to both; it has
no committed default. For a disposable local shell, for example:

```bash
export RUNNER_AUTH_TOKEN="$(openssl rand -hex 32)"
docker compose -f tests/integration/compose/docker-compose.yml up -d --build
```

Keep the value consistent when recreating either service. Runner readiness
fails if its secret is absent. The daemon writes fixed diagnostic fixtures to a
shared volume; Runner mounts it read-only and has a separate durable data volume.
Runner is non-root and has no published host port or Docker socket. Run the
isolated, non-destructive local acceptance with `bash tests/e2e/04-runner.sh`;
it creates a test robot and leaves that robot in the selected organization.
The full manual flow and output-offset behavior are in `docs/human-testing/04.md`.

For the Agent, daemon, Runner, and Base101 simulator, use the sibling
`ringfour-virtual` project. The physical non-motion smoke recorded in
`docs/evidence/04.md` used Base101-maintained deployment configuration; this
repository no longer owns robot Compose manifests or Balena build staging.

## Script execution (milestone 05)

The API stores uploaded revisions and verified artifacts through a private
filesystem or S3-compatible backend. Local Compose uses the persistent
`api-storage` volume; hosted configuration and CLI examples are in
`docs/human-testing/05.md`. `tests/e2e/05-scripts.sh` runs a disposable full
stack with real Auth0 login; set `RINGFOUR_TEST_STORAGE=garage` to exercise
Garage. The script does not remove or reset the existing development stack.
See `docs/scripts-contract.md` and `docs/evidence/05.md` for the new contracts,
limits, restart behavior and recorded tests.

## Device connectivity and operations (milestone 03)

The daemon opens an authenticated WebSocket to `GET /v1/device/connect`
(credential in `x-ringfour-credential-id` / `x-ringfour-credential-secret`
headers) right after enrollment and reconnects with exponential backoff +
jitter. API configuration: `Connectivity__HeartbeatIntervalSeconds` (15),
`Connectivity__OfflineThresholdSeconds` (45), `Connectivity__OperationDeadlineSeconds`
(60), `Connectivity__SweepIntervalSeconds` (2), `Idempotency__TtlHours` (24).
Daemon configuration: `DAEMON_BACKOFF_INITIAL_SECONDS` (1),
`DAEMON_BACKOFF_MAX_SECONDS` (30), `DAEMON_RECONCILE_DELAY_SECONDS` (20),
`DAEMON_OUTBOUND_QUEUE_LIMIT` (128).

The API emits structured session and operation lifecycle logs without credentials.
The `Ringfour.Connectivity` .NET Meter exposes `ringfour.device.connected`,
`ringfour.device.connection_attempts`, `ringfour.operation.queued`,
`ringfour.operation.oldest_queued_seconds`, and `ringfour.operation.terminal`
(the last tagged by state). Connection attempts include initial connections and
retries; the daemon logs retry attempt numbers per robot. Queue gauges are updated
on each successful sweep and can become stale while PostgreSQL is unavailable.
These instruments require a .NET diagnostics or OpenTelemetry collector; no
metrics exporter or monitoring backend is included in the local stack.

Human endpoints: `GET /v1/robots/{id}/status` (member read),
`POST /v1/robots/{id}/operations` (developer+; v1 kind `diagnostic.noop`
only; optional `Idempotency-Key` header replays identical retries and
conflicts on divergence), `GET /v1/robots/{id}/operations/{op_id}`.
CLI: `ringfour-cli robot status <robot-id>`. See `docs/human-testing/03.md` for
the manual flow and `docs/evidence/03.md` for acceptance results.

## Live Logbook acceptance (milestone 08a)

The selected Console timeline polls the existing organization history endpoint
every three seconds; 08a adds no new wire contract. Run the isolated real-process
acceptance with live Auth0 credentials, or use the API's Development-only test
signer with already-built local images:

```sh
RINGFOUR_TEST_LOGBOOK=1 bash tests/e2e/07-cli-dev.sh
RINGFOUR_TEST_SKIP_BUILD=1 RINGFOUR_TEST_LOCAL_AUTH=1 \
  RINGFOUR_TEST_LOGBOOK=1 bash tests/e2e/07-cli-dev.sh
```

The test verifies two mission-authored entries through Runner, daemon, API,
PostgreSQL and the authorized history endpoint while the execution is still
running, then stops it. Test signing mode cannot start in hosted configuration
and is not evidence for the separate live Auth0 gate. See
`docs/human-testing/08.md` and `docs/evidence/08.md`.


## Layout

- `apps/api` — C# ASP.NET Core modular monolith (.NET 10 LTS, pinned SDK image)
- `apps/daemon` — Python FastAPI device coordinator, durable SQLite
- `apps/runner` — Python FastAPI execution runner, private (no published port)
- `apps/cli` — Python Typer/Rich CLI, the only host-run component
- `apps/console` — React/TS/Vite internal dev console (development-only)
- `packages/contracts` — OpenAPI + JSON Schema + fixtures, language-independent
- `infra/server` — independent server-side Compose deployment
- `../ringfour-virtual` — Base101 simulator and device-side Compose stack
- `tests/integration/compose` — legacy integrated acceptance fixture
- `tests/e2e` — integrated acceptance scripts

## Device pairing page

An unenrolled daemon starts a pairing request and shows its code on the
read-only `http://<device-ip>:8083/` page and in its logs. Claim it as an owner using **Robots → Add a robot** in the Console. Enter a
name to create the robot as part of pairing, or choose an existing unpaired
robot.
The page shows only the code or enrolled connection state; control API routes
are not served on that port. Restrict page access to a trusted network while
pairing. The daemon persists the pending request and machine credential in its
own volume with owner-only permissions. Use HTTPS for the Platform URL outside a
controlled local test. The API's per-process pairing limiter is keyed by the
observed client IP; configure trusted proxies and edge limits appropriately for
hosted deployments. See `docs/pairing-contract.md` and `docs/human-testing/02.md`.
