# Ringfour — progress log

Running log of work performed on this repository. Updated continuously as tasks are
completed; see `docs/evidence/NN.md` for per-milestone acceptance records. No secrets
or tokens in this file.

## 2026-11-09 — session: setup assessment + logging rule

- Read `README.md`, `docs/architecture.md`, `docs/milestones/00-foundation.md`,
  `docs/milestones/01-identity.md` (remaining milestones 02–10 listed, not yet read).
- Assessed local toolchain against milestone 00 requirements:
  - Present: Docker 29.9.2, Docker Compose v5.5.1, uv 0.12.1, git 2.54.0, Python 3.9.6 (system).
  - Missing: Node.js (required for API — Fastify/TypeScript/Drizzle — and Console — React/Vite). Node LTS must be installed before milestone 00 scaffolding.
- Open decision recorded: Node package manager for the monorepo (npm workspaces vs pnpm) — pick at 00 scaffolding; docs do not specify.
- Auth0 identified as the external gate for milestone 01 (not 00): needs a real tenant with an API (audience), a Native app for the CLI (PKCE or Device Authorization), a SPA app for the Console (Auth Code + PKCE), RS256/issuer/expiry validation config, and two test user accounts for the cross-org isolation acceptance. Local test signing keys are test-only and must be rejected by hosted config.
- Later external gates noted: Balena smoke at 04; real robot + version-pinned SDK/Agent/ROS at 10.
- Added mandatory progress-logging rule to `AGENTS.md` (every task logged here; no batching) and created this `PROGRESS.md`.
- Next: install Node.js LTS, then begin milestone 00 (foundation scaffolding).

## 2026-11-09 — task: .gitignore + secret protection

- Created `.gitignore` covering secrets (`.auth.env` / `*.auth.env` first-class, plus `.env*` with `.example` negations), Node, Python, local runtime state, test artifacts, OS/editor noise.
- Verified pre-existing `.auth.env` on disk was untracked before the ignore rule landed — it now resolves via `git check-ignore` and no longer appears in `git status`. No secret file was ever staged or committed.
- Noted for later: two `.DS_Store` files were already tracked in git (now shown as deleted in status); unrelated to this task, left as-is.

## 2026-11-09 — milestone 00 start: toolchain resolved + key decisions

- Node.js found at `/opt/homebrew/bin/node` (Homebrew `node@26`, v26.13.0, npm 11.20.0); earlier "missing" was a non-interactive-shell PATH issue. No install needed. Commands must prefix `PATH=/opt/homebrew/bin:$PATH`.
- Decisions (recorded per AGENTS.md step 1, no suitable existing API to reuse):
  - **No JS workspace manager**: each app/packages dir gets its own `package.json` + `package-lock.json`, satisfying the milestone's "independent app packages and lockfiles" and later extraction. Root `package.json` is scripts-only orchestration.
  - **Python apps via uv** (0.12.1), Python 3.13, hatchling src-layout, version read from `__init__.py` via importlib.metadata.
  - **API component tests use PGlite** (isolated real PostgreSQL, WASM) for ready-ok; ready-fail uses the real `pg` driver against a closed port. Live PG only at integration tier.
  - **Health contract**: live → `{status:"ok",version}`; ready → `{status,version,checks{name:"ok"|"fail"}}`, HTTP 503 when any check fails. Versioned as `contracts/health/v1`.
  - **Console talks to API/daemon through the Vite dev proxy** — no CORS needed; health page gated on `import.meta.env.DEV`.
  - **Runner has no published compose port** (private); daemon readiness checks Runner, CLI checks API+daemon.
- Starting milestone 00 (Foundation): contracts first, then apps, compose, acceptance.
- User directive: **Docker-first** — API, PostgreSQL, daemon, Runner and Console all run in Docker Compose from the start (daemon was already specced as a container). Only the CLI runs on the host. Host-side tooling (node/uv) is for lint/typecheck/component tests; integrated runtime is always Compose.

## 2026-11-09 — task: Platform API re-scaffolded in C# (.NET 10, all tooling in Docker)

- Context: stack decision changed from Fastify/TypeScript/Drizzle to C#/ASP.NET Core/EF Core (README/architecture/AGENTS updated in the same working tree); the old `apps/api` was deleted. This task rebuilt the milestone-00 API slice in C#.
- **No host .NET SDK**: everything runs in Docker. Pinned images `mcr.microsoft.com/dotnet/sdk:10.0` (SDK 10.0.403) and `mcr.microsoft.com/dotnet/aspnet:10.0` (runtime 10.0.14). `scripts/dotnet.sh` wraps the SDK container; NuGet packages, dotnet tools and per-project `obj`/`bin` live in named volumes (`ringfour-dotnet-*`, `ringfour-api-*`) because container-written files on the macOS bind mount produced bogus mtimes (MSBuild `DateTimeOffset` overflow) and stale file handles.
- Scaffolded `apps/api/`: `Ringfour.slnx`, `src/Ringfour.Api` (Minimal API), `tests/Ringfour.Api.Tests` (xUnit). Structure follows README repo layout. Shared settings (net10.0, nullable, warnings-as-errors, Version 0.1.0, no source revision in InformationalVersion) in `apps/api/Directory.Build.props`.
- Implemented `GET /health/live` and `GET /health/ready` per `contracts/health/v1` (ready checks PostgreSQL via EF `CanConnectAsync`, 503 with `checks.postgresql=fail` when down). EF Core 10.0.0 + Npgsql 10.0.0; checked-in initial migration `Persistence/Migrations/*_InitialCreate.cs` (empty baseline); API applies migrations at startup (`Database:MigrateOnStartup`, default true); `DesignTimeDbContextFactory` keeps `dotnet ef` off `Program.cs`.
- NuGet audit (warnings-as-errors) caught vulnerable transitives; pinned overrides: `System.Security.Cryptography.Pkcs` 9.0.* (via Npgsql) and `System.Security.Cryptography.Xml` 9.0.* (test stack); Testcontainers.PostgreSql pinned 4.16.0 (4.1.0 pulled vulnerable SSH.NET 2024.1.0).
- Component tests (xUnit + Testcontainers.PostgreSql, postgres:18): liveness shape/version, readiness ok + exact payload shape, migrations applied from empty DB (`__EFMigrationsHistory` contains InitialCreate), readiness 503/fail with PostgreSQL down. Result: 4/4 pass via `scripts/test-component.sh`. The test SDK container mounts the host Docker socket (dev tooling only; the runtime stack never does) and uses `TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal` to reach sibling containers.
- `apps/api/Dockerfile`: multi-stage sdk→aspnet, non-root `app` user, port 8080; image `ringfour-api:dev` builds clean.
- Real-container smoke (manual, pre-Compose): postgres:18 + `ringfour-api:dev` on a Docker network — live/ready 200, migration applied; stopping postgres → ready 503 `{postgresql: fail}`, live stays 200; restarting postgres with the same volume → ready recovers 200. Noted: postgres:18 images expect the data volume at `/var/lib/postgresql` (not `/var/lib/postgresql/data`) — relevant for Compose.
- Rewired `scripts/setup.sh`/`lint.sh`/`typecheck.sh`/`test-component.sh` for dockerized dotnet (`dotnet format --verify-no-changes` = lint, `dotnet build` = typecheck, `dotnet test` = component); Python/Node apps guarded until they exist. `.env.example`: `DATABASE_URL` replaced with `ConnectionStrings__Postgres`. `.gitignore`: added .NET `bin/`/`obj/`.
- Verified: `scripts/lint.sh`, `scripts/typecheck.sh`, `scripts/test-component.sh` all green.
- Milestone 00 remaining: daemon, runner, cli, console apps; `infra/compose` stack (with pg healthcheck gating the API — the bare API crashes if postgres is not up at startup, by design); real-process integrated acceptance; `docs/evidence/00.md`.

## 2026-11-09 — task: live contract-schema validation wired into API tests

- API xUnit tests now validate real HTTP response bodies against the actual `packages/contracts/schemas/**` files (JsonSchema.Net 9.6.0, draft 2020-12), not just hand-written shape assertions. `ContractSchema.AssertValid(schemaPath, json)` helper; schemas compiled once per path (JsonSchema.Net registers `$id` globally — duplicate `FromText` throws "Overwriting registered schemas is not permitted").
- Test project links `packages/contracts/schemas/**/*.json` as content copied to test output. Consequence: the SDK container now mounts the **repo root** at `/repo` (workdir `/repo/apps/api`) instead of only `apps/api`, so the repo-relative path resolves identically on host and in-container. `scripts/dotnet.sh` and `scripts/test-component.sh` updated; artifact volume targets moved to match.
- Verified: `scripts/test-component.sh` 4/4 pass, `scripts/lint.sh` (dotnet format) clean, `scripts/typecheck.sh` clean.

## 2026-11-09 — milestone 00 continued: daemon, runner, cli apps + component tests

- `apps/daemon`: FastAPI + uvicorn, Python 3.13 via uv (hatchling src-layout, version from `__init__.py`). Durable SQLite with a minimal ordered-migration runner (`db.py`, `schema_migrations` table, `migrations/0001_baseline.sql` baseline). Health per `contracts/health/v1`: readiness checks `sqlite` (connect + BEGIN IMMEDIATE write probe) and `runner` (GET runner `/health/ready`, 2s timeout). Migrations run at startup; unusable DB dir fails fast. Runner HTTP client injectable for tests. 6/6 pytest + ruff clean.
- `apps/runner`: FastAPI, private service. Readiness checks `volume` (write probe in `RUNNER_DATA_DIR`) and `uv` (binary on PATH + `uv --version`). 3/3 pytest + ruff clean (uv-gated test skips if uv absent on host).
- `apps/cli`: Typer + Rich + httpx. `ringfour health` probes API + daemon `/health/ready` (env `RINGFOUR_API_URL`/`RINGFOUR_DAEMON_URL`, defaults localhost:8080/8081), Rich table, exit 1 on any fail/unreachable. Typer group callback added so subcommand form survives future commands. 5/5 pytest + ruff clean.
- `apps/console`: Vite 7 + React 19 + TS + Tailwind v4 with shadcn/ui structure (components.json, `cn`, `ui/card`). Dev-gated health page (`import.meta.env.DEV`) polling `/api/health/ready` and `/daemon/health/ready` through the Vite proxy (targets from `API_PROXY_TARGET`/`DAEMON_PROXY_TARGET`, default localhost) — no CORS. Lint via Biome (not ESLint — single dep, covers format+imports; `css.parser.tailwindDirectives` needed for Tailwind v4 syntax). vitest + Testing Library: 3/3 (ok / fail / unreachable rendering).

## 2026-11-09 — milestone 00 complete: compose stack, integrated acceptance, evidence

- Dockerfiles: daemon/runner on `python:3.13-slim` with `uv` 0.12.1 copied from `ghcr.io/astral-sh/uv:0.12.1`, non-root `app` user, `/data` volumes; console on `node:24-alpine` running the Vite dev server (dev-only). API image from earlier task.
- `infra/compose/docker-compose.yml`: postgres:18 (healthcheck, volume at `/var/lib/postgresql`), api (waits on healthy postgres, applies EF migrations at startup), runner (private, `expose` only — no published port), daemon (SQLite volume, `RUNNER_URL=http://runner:8082`), console (Vite proxy targets via env). No Docker socket mounts; no secrets (defaults inline, overridable via `.env`).
- `tests/e2e/00-health.sh` (`npm run test:e2e`): clean start from empty volumes → runner-privacy check → `ringfour health` exit 0 → stop postgres (api 503, CLI exit 1) → restore (recover, no volume deletion) → stop runner (daemon 503, CLI exit 1) → restore → console serves + proxies → final all-ok. **PASSED** on first full run.
- Console aligned to the Ringfour interface system (`ringfour/design/design_system`): dark theme default, token palette (orange #E47A00 accent, teal #36B5A0 ready, red danger) mapped into Tailwind v4 `@theme`, square elevation-1 panels, Space Grotesk titles (fonts vendored with OFL into `apps/console/public/fonts`), Inter via `@fontsource/inter`, status badges per `components.css` pattern. Rule recorded in `AGENTS.md`. Console suites re-verified after the change (3/3 vitest, typecheck, biome).
- Evidence recorded in `docs/evidence/00.md` with pinned versions, commands and observed results. Commands documented in `docs/development.md`.
- Full component suite green: contracts 3/3, api 4/4, console 3/3, daemon 6/6, runner 3/3, cli 5/5; lint + typecheck clean.
- Milestone 00 gates: no external prerequisites required (Auth0 is 01). Next: milestone 01 (identity) — needs a real Auth0 tenant for its external acceptance gate.

## 2026-11-09 — milestone 01: identity

- Docs update absorbed first: `docs/database-schema.md` (starter persistence contract for 00–03) arrived user-authored; restored accidentally deleted `docs/development.md`/`docs/evidence/00.md` and the design-system rule in `AGENTS.md`; committed as a docs commit (`ce5b3e5`). New standing rule: every milestone ships `docs/human-testing/NN.md`.
- Contracts first: identity schemas (user, organization, membership, me/org-list/member-list, create/add/update requests), error fixtures (`unauthenticated`, `forbidden`, `last_owner`), OpenAPI `/v1/me`, `/v1/organizations`, member item routes with bearer security. Cursor pagination (`limit` ≤ 200, opaque cursor). Contracts suite 5/5.
- API: JWT Bearer RS256 (Auth0 metadata mode + test-only PEM mode refused in Production), `/v1/me` resolve-or-create with unique-violation retry (concurrent first login verified), org create with atomic owner membership, member list/add/patch/remove with owner-only mutation, cross-org 404 (not 403), last-owner protection via org-row `FOR UPDATE` lock, opaque cursor pagination, shared error envelope middleware, snake_case + `Z`-timestamp JSON. EF migration `Identity` (users/organizations/memberships per starter schema; roles as constrained strings). 28/28 xUnit incl. JWT negatives, startup guards, concurrency.
- CLI: `login` (device authorization, `--no-browser` option), `logout`, `whoami`, `org create/list/select`; tokens in macOS Keychain via keyring 25.7.0, selected org in config file, 401→refresh→retry. 15/15 pytest, ruff clean.
- Console: Auth Code + PKCE via @auth0/auth0-react 2.30.0; login gate with signup (`screen_hint`), Identity view (me, orgs, create org, member inspection), auth errors surfaced. 7/7 vitest; typecheck/lint clean.
- Live Auth0 acceptance: three tenant gates found and fixed (Device Code grant; API user-delegated access policy `allow_all` — default `require_client_grant` blocked user flows; Web app type corrected to SPA → token endpoint stopped demanding a client secret). Device login ×2 accounts; stable `whoami`; org round-trip; **live cross-account isolation: account 2 reading account 1's org → 404 not_found**; Console PKCE sign-in verified by user. `tests/e2e/01-identity.sh` PASS.
- Daemon/runner: out of scope for 01 (no human tokens; machine credentials arrive in 02) — regression suites 6/6 and 3/3.
- Evidence in `docs/evidence/01.md`, human guide in `docs/human-testing/01.md`, commands in `docs/development.md`. Definition of done met; committing milestone 01.
- Post-commit recheck (user request): second-account isolation re-run live — account 2 distinct user id, empty org list, 404 not_found on both member read and member write against account 1's organization. Evidence §6 updated.

## 2026-11-10 — milestone 02: robot enrollment

- Contracts first: robot/enrollment/credential schemas under `packages/contracts/schemas/robots/v1/`, `invalid_token` error fixture, OpenAPI `robots` + `device` tags (the two `/v1/device/*` endpoints are `security: []` by design). New device endpoint `POST /v1/device/authenticate` classified as new design (needed to prove revocation/expiry before 03). Contracts suite 7/7.
- API: entities + EF migration `Robots` (robots, enrollment_tokens with unique token_hash, robot_credentials with unique secret_hash, robot_audit_events with (robot_id, occurred_at, id) index; Restrict deletes; audit-type check constraint). Endpoints: robot create/list/show, enrollment-token issue, credentials list/rotate/revoke (owner-only admin; member read; non-member 404), device enroll/authenticate. Secrets are `rft_`/`rfs_` + 32 random bytes, only SHA-256 hex stored; token consumption is one conditional UPDATE … RETURNING in a transaction with credential insert + enrolled_at + audit (concurrent race → exactly one winner). Rotation bounds the overlap by setting the old credential's expires_at (config `Enrollment:RotationOverlapSeconds`, default 300); token TTL config `Enrollment:TokenTtlMinutes` (default 15). Shared helpers extracted: `IdentityAccess`, `Pagination` (identity module refactored onto them, behavior unchanged).
- Daemon: SQLite migration `0002_device_identity.sql` (singleton CHECK), `POST /v1/enroll` + `GET /v1/identity`; secret written to `/data/robot-credential` 0600 via atomic replace **before** the identity row commits; boot reports missing credential as `credential_missing`. Compose: daemon gets `PLATFORM_API_URL=http://api:8080`, depends on api.
- CLI: `robot create/list/show/enrollment-token/credentials/rotate/revoke`, `daemon enroll/identity`.
- Tests: api 37/37 (9 new: role matrix, concurrent enroll single-winner, expired/reused/garbage token 401, overlap, revoke idempotency, contract validation, hash redaction, pagination); daemon 12/12 (6 new); cli 27/27 (12 new); contracts 7/7; console/runner regression green; lint + typecheck clean.
- Integrated acceptance `tests/e2e/02-enrollment.sh` **PASS** (repeatable; resets the daemon volume first): device endpoints reject garbage unauthenticated, owner creates robot, daemon enrolls via CLI, identity survives `compose restart daemon`, consumed-token reuse → 401 invalid_token, rotation overlap (old secret read from the daemon volume still authenticates alongside the new one), revoke → immediate 401. Live DB spot-check: all five audit event types, `enrolled` actor-less, only 64-char SHA-256 hashes stored.
- Evidence `docs/evidence/02.md`, human guide `docs/human-testing/02.md`, dev commands updated. Console robot inspector deliberately skipped (optional in the milestone). Idempotency-Key remains deferred to 03.

## 2026-11-10 — milestone 02 follow-up: Console robot inspector

- `apps/console` Robots view (design-system tokens, Card/StatusBadge): organization picker (from `/v1/organizations`, role shown), robot list with enrolled/not-enrolled badges, owner-only create form, robot detail with credentials table (active/revoked badges), owner-only actions: issue enrollment token, rotate credential, revoke. One-time secrets (enrollment token, rotated secret) render in a dismiss-once panel and are never re-fetchable — matching the API contract.
- Owner-only controls are hidden for non-owner members (role from the org list); the API still enforces authorization server-side.
- Tests: 5 new vitest cases (list + status, create, token shown once + dismiss, rotate + revoke calls, read-only for non-owner); 12/12 pass, typecheck + biome clean. Live smoke: Vite transforms and serves the new module in the running stack; interactive sign-in verification is in `docs/human-testing/02.md` §9.
- Test-infra note: component mocks of `useAuth0` must keep `getAccessTokenSilently` referentially stable (module scope), otherwise effects re-run and wipe local selection state.

## 2026-11-10 — milestone 03: connectivity and durable operations

- Contracts first: device WebSocket protocol schemas under `schemas/device/v1/` (envelope, hello, welcome, heartbeat, command, ack, operation-status, protocol-error) + message fixtures; robots schemas `operation`, `create-operation-request`, `robot-status-response` + fixtures; OpenAPI status/operations paths, `Idempotency-Key` header param and the informational `/v1/device/connect` entry. Two new platform-originated message types (`welcome`, `error`) extend the architecture's enumerated set — classified as new design in evidence/03.md. Contracts suite 10/10.
- API: EF migration `Connectivity` (robot_presence with fenced session updates, operations with (robot_id, state, created_at, id) index and state check constraint, operation_idempotency_records with unique principal/route/target/key). Authenticated WebSocket `/v1/device/connect` (credential headers, 4401/4402/4409 closes, per-connection sequence discipline, session replacement with session_replaced notice, replay of nonterminal operations after welcome); hosted OperationSweeper (deadline expiry + queued redelivery to live sessions); HTTP endpoints `GET /v1/robots/{id}/status` (server-receive-time freshness window, 45 s), `POST/GET /v1/robots/{id}/operations[/{op}]` with optional Idempotency-Key (sha256 body fingerprint, 24 h TTL, replay/conflict); revocation now closes live sessions (persist-before-disconnect) and the handshake denies revoked credentials. Config frozen in appsettings (`Connectivity:*`, `Idempotency:TtlHours`). 53/53 xUnit (16 new) — protocol over TestServer WebSockets.
- Real bugs found by tests and fixed: (1) revoke endpoint awaited the WS close handshake → HTTP/device deadlock (CloseOutputAsync now); (2) `Enum.TryParse` case-sensitivity rejected contract-lowercase operation states; (3) JsonNode DOM payloads ignore serializer converters → timestamps pre-formatted with `RingfourJson.UtcTimestamp`; (4) WS sends are async → tests poll durable state instead of assuming transitions landed.
- Daemon: SQLite migration 0003 `device_operations` (dedup/reconciliation store), `operations.py` (record-before-ack, identical replay, conflicting id = protocol error, terminal immutability, deadline expiry, superseded reconciliation), `connection.py` ConnectionManager (enrollment poll 5 s, exponential backoff+jitter reconnect, negotiated heartbeat, bounded outbound queue, restart-safe dedup), lifespan wiring in `main.py`; new dependency `websockets>=14.0`. 31/31 pytest (19 new) against a real localhost websockets stub.
- CLI: `ringfour robot status` (online/offline, last seen, daemon/protocol versions). 28/28 pytest.
- Console: Robots detail panel gains a polling Device status inspector (online/offline badge, last seen, versions) per the design system; 14/14 vitest.
- Integrated acceptance `tests/e2e/03-connectivity.sh` **PASS** (run twice: full pass on rerun after a log-assertion race was fixed; live diagnostic op, idempotency replay, network-partition offline after the 45 s threshold, queued op runs once on reconnect, restart with single local dedup row, revocation closes the session and denies reconnect). Human guide `docs/human-testing/03.md`; dev commands updated; `test:e2e:03` registered. Full component suite green: contracts 10/10, api 53/53, daemon 31/31, cli 28/28, console 14/14, runner 3/3; lint + typecheck clean.
