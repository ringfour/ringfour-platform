# Platform development

This repository implements the Ringfour Platform API, device Daemon, CLI and
internal Console. See [Apps architecture](architecture.md) for component responsibilities.

## Prerequisites

- .NET SDK version recorded by the API project.
- Python 3.13 or newer and `uv` for Python projects.
- Node.js and npm for the Console.
- Docker Engine for PostgreSQL Testcontainers and registry integration tests.
- Auth0 CLI application settings for interactive CLI login.

Use each project's committed lockfile. The SDK, Agent and virtual simulator are
separate sibling repositories; the Platform does not vendor their source.

## Local services

The local server stack is managed from the repository root. Configure the
ignored `.env` for local Auth0 and registry settings, then start or
reload it as an operator action:

```sh
./up.sh -d api console app-registry registry-ui
```

Studio is available at `http://localhost:5173`. Set `AUTH0_DOMAIN` and
`AUTH0_WEB_CLIENT_ID` for its SPA configuration. The local API is normally available at `http://localhost:8080`; the local image
registry is `127.0.0.1:5000` and its browser UI is at `http://localhost:5080`.
Use the [registry setup guide](local-registry.md) for its
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

## Check the CLI and Daemon installations

From the repository root:

```sh
uv sync --project apps/cli --frozen
uv run --project apps/cli ringfour-cli --help

uv sync --project apps/daemon --frozen
uv run --frozen --project apps/daemon python -c "from importlib.metadata import version; print(version('ringfour-daemon'))"
```

The Daemon's App runtime and engine configuration are described in
[engine operations](apps-engine.md), [hosted runs](apps-runs.md), and
[runtime evidence](apps-runtime.md). See the [virtual deployment guide](../../ringfour-virtual/README.md) for the simulated device.

## Database migrations

API persistence uses EF Core/Npgsql migrations against PostgreSQL. Review
[`database-schema.md`](database-schema.md) before changing storage and its migrations. The Daemon keeps durable local state in SQLite and
applies its ordered migrations on startup. Do not reset volumes to apply an
ordinary migration.

## Component checks

Run these commands from the Platform checkout. Use Node.js 24, the API's .NET 10 SDK, and Python 3.13 or later. API integration tests require access to Docker for disposable PostgreSQL and registry fixtures.

```sh
npm ci --prefix packages/contracts
npm --prefix packages/contracts test
uv run --with-requirements packages/contracts/requirements-test.txt python packages/contracts/test/openapi_refs.py
npm ci --prefix apps/console
npm --prefix apps/console test
npm --prefix apps/console run lint
npm --prefix apps/console run build
dotnet test apps/api/Ringfour.slnx
uv run --frozen --project apps/cli pytest -q apps/cli/tests
uv run --frozen --project apps/daemon pytest -q apps/daemon/tests
uv run --project packages/python-runtime --with pytest pytest -q packages/python-runtime/tests
```

Additional integration checks exercise the [API catalog and private registry](../tests/integration/api-catalog.py), [Daemon engine recovery](../tests/integration/daemon-engine.py), [Python distribution bundle](../tests/integration/python-bundle.py), and [Garage storage adapter](../tests/integration/garage-probe/Program.cs). Each requires its documented service fixtures. Cross-application Platform acceptance is deferred.
