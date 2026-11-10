# Development commands

Established in milestone 00. Runtime services always run in Docker Compose; host tooling
(Node, uv) is used only for lint/typecheck/component tests. The .NET SDK runs in Docker
via `scripts/dotnet.sh`; no host .NET installation is required or supported.

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
| `npm run test:e2e:01` | Milestone 01 identity acceptance (requires a completed `ringfour login`) |
| `npm run test:e2e:02` | Milestone 02 robot enrollment acceptance (requires `ringfour login`; resets the daemon volume) |
| `npm run test:e2e:03` | Milestone 03 connectivity acceptance (requires `ringfour login`; resets the daemon volume; ~4 min, includes two 45 s offline-threshold waits) |
| `npm run compose:up` | Build and start the full stack (postgres, api, daemon, runner, console) |
| `npm run compose:down` | Stop the stack (volumes kept) |
| `npm run compose:clean` | Stop and delete all volumes (fresh start) |
| `npm run compose:logs` | Follow stack logs |

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
`AUTH0_AUDIENCE`, `AUTH0_CLI_CLIENT_ID`, `AUTH0_WEB_CLIENT_ID`. Compose reads it via the
gitignored symlink `infra/compose/.env`; the API gets `Auth0__Domain`/`Auth0__Audience`
and the console `VITE_AUTH0_*` from it.

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

CLI commands: `ringfour robot create|list|show|status|enrollment-token|credentials|rotate|revoke`
(owner-only administration; members can read) and `ringfour daemon enroll|identity`
(local daemon, no login needed). See `docs/human-testing/02.md` for the manual
flow and `docs/evidence/02.md` for acceptance results.

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

Human endpoints: `GET /v1/robots/{id}/status` (member read),
`POST /v1/robots/{id}/operations` (developer+; v1 kind `diagnostic.noop`
only; optional `Idempotency-Key` header replays identical retries and
conflicts on divergence), `GET /v1/robots/{id}/operations/{op_id}`.
CLI: `ringfour robot status <robot-id>`. See `docs/human-testing/03.md` for
the manual flow and `docs/evidence/03.md` for acceptance results.


## Layout

- `apps/api` — C# ASP.NET Core modular monolith (.NET 10 LTS, pinned SDK image)
- `apps/daemon` — Python FastAPI device coordinator, durable SQLite
- `apps/runner` — Python FastAPI execution runner, private (no published port)
- `apps/cli` — Python Typer/Rich CLI, the only host-run component
- `apps/console` — React/TS/Vite internal dev console (development-only)
- `packages/contracts` — OpenAPI + JSON Schema + fixtures, language-independent
- `infra/compose` — local stack
- `tests/e2e` — integrated acceptance scripts
