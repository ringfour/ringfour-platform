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


## Layout

- `apps/api` — C# ASP.NET Core modular monolith (.NET 10 LTS, pinned SDK image)
- `apps/daemon` — Python FastAPI device coordinator, durable SQLite
- `apps/runner` — Python FastAPI execution runner, private (no published port)
- `apps/cli` — Python Typer/Rich CLI, the only host-run component
- `apps/console` — React/TS/Vite internal dev console (development-only)
- `packages/contracts` — OpenAPI + JSON Schema + fixtures, language-independent
- `infra/compose` — local stack
- `tests/e2e` — integrated acceptance scripts
