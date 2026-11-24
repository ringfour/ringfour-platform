# Platform development

This repository implements the Ringfour Platform API, device Daemon, CLI and
internal Console. The active Apps architecture and milestone order are in the
[Apps pivot plan](milestones/apps-pivot.md). Historical mission/source-upload
workflows are preserved in the [archive](archive/legacy-mission-workflows/README.md).

## Prerequisites

- .NET SDK version recorded by the API project.
- Python 3.13 or newer and `uv` for Python projects.
- Node.js and npm for the Console.
- Docker Engine for PostgreSQL Testcontainers and isolated protocol acceptance.
- Auth0 CLI application settings for interactive CLI login.

Use each project's committed lockfile. The SDK, Agent and virtual simulator are
separate sibling repositories; the Platform does not vendor their source.

## Local services

The local server stack is managed from the repository root. Configure the
ignored `Platform.env` for local Auth0 and registry settings, then start or
reload it as an operator action:

```sh
./up.sh -d api app-registry registry-ui
```

The local API is normally available at `http://localhost:8080`; the local image
registry is `127.0.0.1:5000` and its browser UI is at `http://localhost:5080`.
Use the [registry acceptance guide](human-testing/local-registry.md) for its
same-origin proxy and CORS behavior. The virtual Agent/Daemon stack is maintained
in the sibling `ringfour-virtual` repository.

Stack rebuilds and reloads are operator-controlled under the workspace
[`AGENTS.md`](../AGENTS.md). Keep persistent volumes and enrolled device
identity during ordinary updates.

## Auth0 setup

The CLI uses OAuth Device Authorization Flow. Configure the CLI's Auth0 domain,
client ID, and API audience in the user's environment or config file; do not
place access tokens in repository files. The usual per-user CLI config path is
`~/.config/ringfour/config.json`. See the
[CLI setup](../apps/cli/README.md#install-and-sign-in) and the
[pairing guide](pairing-contract.md) for device enrollment.

## Run applications

From the repository root:

```sh
uv sync --project apps/cli --frozen
uv run --project apps/cli ringfour-cli --help

uv sync --project apps/daemon --frozen
uv run --project apps/daemon ringfour-daemon --help
```

The Daemon's App runtime and engine configuration are described in
[engine operations](apps-engine.md), [hosted runs](apps-runs.md), and
[runtime evidence](apps-runtime.md). The opt-in virtual deployment guide is
[AP-08V](human-testing/AP-08V.md). The default Runner deployment remains a
migration path until AP-14/AP-15 acceptance retires it.

## Database migrations

API persistence uses EF Core/Npgsql migrations against PostgreSQL. Review
[`database-schema.md`](database-schema.md) before changing storage and add new
tables in their assigned AP. The Daemon keeps durable local state in SQLite and
applies its ordered migrations on startup. Do not reset volumes to apply an
ordinary migration.

## Component checks

Run only the affected project checks for the change under development. Common
commands include:

```sh
dotnet test apps/api/tests/Ringfour.Api.Tests/Ringfour.Api.Tests.csproj
uv run --project apps/daemon pytest -q apps/daemon/tests
uv run --project apps/cli pytest -q apps/cli/tests
```

The Apps milestone plan records required real-protocol acceptance. Its E2E
harnesses use disposable fixtures; they do not reload the live Platform or
virtual stacks. Current operator checks are collected in
[AP-08/09/10](human-testing/AP-08-10.md) and
[AP-08V](human-testing/AP-08V.md).

## Legacy compatibility

The fixed Runner, mission, and development-session interfaces remain in the
repository for legacy callers and regression coverage until cutover. Their
historical specifications and setup walkthroughs are in
[`docs/archive/legacy-mission-workflows`](archive/legacy-mission-workflows/README.md).
