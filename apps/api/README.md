# Platform API

The API is the hosted Ringfour control plane: a .NET 10 ASP.NET Core modular
monolith backed by PostgreSQL. It verifies Auth0 access tokens, enforces local
organization roles, stores robot and mission metadata, coordinates durable
operations and mediates access to private source and artifact storage. Device
credentials are separate from human tokens. Its public contract is maintained in
[`packages/contracts`](../../packages/contracts/), independently of this app.

## Get started

Use the [local stack instructions](../../docs/development.md) to configure Auth0
and start Compose. From the repository root, after setting the required
`RUNNER_AUTH_TOKEN` and Auth0 values:

```sh
docker compose -f infra/compose/docker-compose.yml up -d --build
curl http://localhost:8080/health/ready
```

The API Dockerfile builds with the .NET 10 SDK. Compose supplies PostgreSQL and
uses a persistent filesystem artifact store for local development; hosted
storage configuration is documented in [script testing](../../docs/human-testing/05.md).
Do not disable authentication to work locally.

## Develop

`src/Ringfour.Api/` is organized by feature (`Identity`, `Robots`,
`Connectivity`, `Storage`, `Missions`) with EF Core entities and migrations in
`Persistence`. Start with [architecture](../../docs/architecture.md),
[database schema](../../docs/database-schema.md) and the relevant milestone
before changing a contract or persistence. The SDK helper builds without a host
.NET install:

```sh
bash scripts/dotnet.sh build
```

Run API tests with .NET 10 and Docker available for PostgreSQL Testcontainers:

```sh
dotnet test apps/api
```

See [development setup](../../docs/development.md) for migrations and the
milestone evidence files for exact integrated test setups.
