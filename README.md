# Ringfour Platform

Ringfour is a hosted platform for publishing and operating robotics Apps.
Humans use the authenticated Platform API and CLI; an enrolled robot Daemon
receives authorized work and manages digest-pinned App containers on its
operator-provided Docker Engine. The Agent owns ROS, robot capabilities and
safety.

## Architecture

- `apps/api`: ASP.NET Core API, Auth0 JWT validation, PostgreSQL and EF Core.
- `apps/daemon`: Python device coordinator with durable SQLite state.
- `apps/cli`: Python CLI for login, organization/device inspection and App
  authoring/publication.
- `apps/console`: Studio for managing workspaces, robots, installations, runs and evidence.
- `packages/contracts`: versioned OpenAPI, JSON Schema and fixtures.
- `infra/server`: local API, PostgreSQL and registry/UI Compose stack.

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
- Use [local registry setup](docs/local-registry.md) for
  development image publication.
- Run the [component checks](docs/development.md#component-checks) for the affected application.
