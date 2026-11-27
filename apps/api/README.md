# Platform API

ASP.NET Core API for Auth0-authenticated users, workspaces, App publication,
installations, hosted runs, durable runtime evidence and robot coordination.
PostgreSQL/EF Core owns hosted records; each robot Daemon owns its local SQLite
state and connects using its enrolled machine credential.

Read the [architecture](../../docs/architecture.md),
[Apps API contract](../../docs/apps-api-contract.md), and
[database schema](../../docs/database-schema.md) before changing endpoints or
persistence.

## Local development

Use [Platform development setup](../../docs/development.md) for Auth0, local
PostgreSQL and server Compose configuration. Authentication remains enabled in
local development.

Build and run the API tests from the repository root:

```sh
dotnet build apps/api/src/Ringfour.Api/Ringfour.Api.csproj
dotnet test apps/api/tests/Ringfour.Api.Tests/Ringfour.Api.Tests.csproj
```

The integration suite uses PostgreSQL Testcontainers.
