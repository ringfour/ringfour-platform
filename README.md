# Ringfour Platform

Ringfour is a hosted platform for publishing and operating robotics Apps.
Humans use the authenticated Platform API and CLI; an enrolled robot Daemon
receives authorized work and manages digest-pinned App containers on its
operator-provided Docker Engine. The Agent owns ROS, robot capabilities and
safety.

## Current implementation

The active plan is the [Apps pivot](docs/milestones/apps-pivot.md), governed by
the [product specification](../app-pivot.md) and
[implementation backlog](../apps-pivot-backlog.md). AP-01–10 implement the
catalog, authoring/publication, installation, hosted runs and durable runtime
evidence. AP-08V adds an opt-in Runner-free virtual deployment; its operator
acceptance remains pending. Next is the combined AP-08/09/10/08V confidence pass,
then Studio and App CLI operations. See the
[acceptance guide](docs/human-testing/AP-08-10.md) and
[AP-08V guide](docs/human-testing/AP-08V.md).

The legacy Runner and mission workflows remain during migration. Their old
user guides and milestone records are in the
[mission-era documentation archive](docs/archive/legacy-mission-workflows/README.md).

## Architecture

- `apps/api`: ASP.NET Core API, Auth0 JWT validation, PostgreSQL and EF Core.
- `apps/daemon`: Python device coordinator with durable SQLite state.
- `apps/cli`: Python CLI for login, organization/device inspection and App
  authoring/publication.
- `apps/console`: authenticated internal web interface; App workflow migration
  is tracked in AP-11.
- `apps/runner`: legacy execution service, retained only for migration callers.
- `packages/contracts`: versioned OpenAPI, JSON Schema and fixtures.
- `infra/server`: local API, PostgreSQL and registry/UI Compose stack.
- `tests/e2e`: protocol and process acceptance harnesses.

The SDK, Agent and virtual simulator are separate projects. Applications
communicate through versioned public contracts; one application's private
implementation is not imported by another. The Engine socket belongs only to
the authorized Daemon. It is never mounted into an App or the API.

## Start here

- Read [architecture](docs/architecture.md) and the
  [database schema](docs/database-schema.md) before changing shared boundaries.
- Follow [Platform development setup](docs/development.md).
- Use the [CLI guide](apps/cli/README.md) to sign in and publish an App.
- Use [App authoring](docs/apps-authoring.md) for the standalone project format.
- Use [local registry acceptance](docs/human-testing/local-registry.md) for
  development image publication.
- Follow the active AP guide before any live acceptance. Stack reloads are
  operator-controlled under the workspace [`AGENTS.md`](AGENTS.md).

Historical mission/source-upload contracts remain available under
[`docs/archive`](docs/archive/README.md) for migration and regression work;
they do not describe the current product path.
