# Ringfour v0.1 — implementation specification

Build a hosted robotics application platform in small, sequential vertical slices. This package specifies new Ringfour contracts; it does not claim those endpoints or SDK methods already exist. The source is the referenced “Read Product Definition Link” conversation and the accompanying delivery request.

## Fixed decisions

| Boundary | Decision |
|---|---|
| Account | Mandatory Auth0 account; no anonymous product use or local password system |
| Platform | Hosted-first C# / ASP.NET Core on a supported .NET LTS; modular monolith |
| Platform persistence | PostgreSQL + EF Core / Npgsql; EF Core migrations |
| Platform authentication | Auth0 access tokens validated by ASP.NET Core JWT Bearer |
| Platform background work | ASP.NET Core hosted services backed by durable PostgreSQL workflow state |
| Platform tests | xUnit + Testcontainers with real PostgreSQL |
| Shared contracts | OpenAPI + JSON Schema |
| Device coordinator | Python FastAPI daemon, durable SQLite state |
| Execution | Apps: digest-pinned containers managed by the enrolled Daemon through an operator-provided engine socket. Legacy: fixed Runner and uv until migration acceptance. |
| Developer tool | Python Typer + Rich CLI |
| Console | Internal development-only React + TypeScript + Vite + shadcn/ui; authenticated, deliberate privileges |
| Repository | Monorepo now; independent application dependencies, packaging and release boundaries for later extraction |
| Robotics | Existing SDK and Agent stay separate projects; Agent owns ROS, capabilities and safety |
| Robot profile | Owned by Agent/robot repository; Ringfour references its identity/version, never becomes its authoring authority |

Deployment infrastructure owns Daemon and Agent containers; only the enrolled Daemon manages its robot’s App workloads. The engine socket grants engine authority and is never passed to Apps or the server. Robot deployments remain operator-owned. The Agent owns ROS and safety. Legacy source execution remains in the fixed Runner during migration; uv environments isolate dependencies, not hostile code.

The active implementation plan is [Apps pivot](docs/milestones/apps-pivot.md), governed by [the pivot specification](../app-pivot.md) and [backlog](../apps-pivot-backlog.md). [App manifest v1](docs/apps-contract.md), [API/runtime](docs/apps-api-contract.md) and [device negotiation](docs/apps-device-contract.md) complete the AP-01 contract gate. AP-03 implements scoped inventory/status and compatibility previews. AP-05 adds registry-verified publication, discovery, sharing/imports and machine pull grants; AP-07 adds opt-in durable installation/configuration/update/rollback; hosted run dispatch remains AP-09. AP-04 supplies [independent local Python wheel bundles](docs/python-distribution.md) consumed with `uv`. AP-08 provides the owner-scoped engine prerequisite ([operator guide](docs/human-testing/AP-08.md)); [AP-07 human checks](docs/human-testing/AP-07.md) cover installation and operator deployment. AP-06 adds the standalone Robot Check template and [authoring/publisher CLI](docs/apps-authoring.md); hosted publication and live sensor acceptance remain explicit gates. Existing milestones and source/Runner contracts remain legacy regression references.

## Repository

```text
apps/api/                            # .NET solution, API project and xUnit tests
  src/Ringfour.Api/                  # business modules and EF Core migrations
  tests/Ringfour.Api.Tests/          # unit and Testcontainers coverage
apps/{daemon,runner,cli,console}/     # independently packaged applications
packages/contracts/                  # OpenAPI + JSON Schema and fixtures
infra/server/                            # API, PostgreSQL, Console
tests/integration/compose/               # legacy full-stack E2E fixture
tests/e2e/
docs/architecture.md
docs/milestones/
AGENTS.md
```

Applications communicate through versioned contracts, never imports of another application's implementation. The Ringfour SDK and Agent are independent open-source projects, pinned for integration and not copied into this tree. `../ringfour-virtual` owns the local Base101 simulator/device stack and builds those projects from source. The CLI and internal Console use the same documented Platform API; its implementation language does not affect their contracts. Platform contains no robot or Balena deployment manifests.

## Reading and execution order

Use the [starter database schema](docs/database-schema.md) for milestones 00–03 and its change rules for later persistence work.

Read [AGENTS.md](AGENTS.md), then [architecture](docs/architecture.md) and the active [Apps pivot plan](docs/milestones/apps-pivot.md). The following earlier milestones describe the legacy implementation and its regression obligations:

- [00-foundation.md](docs/milestones/00-foundation.md)
- [01-identity.md](docs/milestones/01-identity.md)
- [02-enrollment.md](docs/milestones/02-enrollment.md)
- [03-connectivity.md](docs/milestones/03-connectivity.md)
- [04-runner.md](docs/milestones/04-runner.md)
- [05-scripts.md](docs/milestones/05-scripts.md)
- [06-missions.md](docs/milestones/06-missions.md)
- [07-dev-sessions.md](docs/milestones/07-dev-sessions.md)
- [08-logbook.md](docs/milestones/08-logbook.md)
- [09-hitl.md](docs/milestones/09-hitl.md)
- [10-robotics-e2e.md](docs/milestones/10-robotics-e2e.md)

## Delivery gate

For every milestone: contract → implementation → independent component tests → real-process integrated acceptance → review → commit. Stop on failures. Each milestone records exact reproducible commands and results in its implementation evidence; command names are established in 00, not assumed to exist today. Local integration must use real PostgreSQL, API, daemon, Runner and CLI where applicable. Auth0 and physical-device checks are separate explicit external acceptance gates, never silently replaced by mocks.

The architecture document defines cross-cutting behavior; milestone contracts add only the current slice. Any implementation-driven change must update schemas, consumers and tests together.

## Business context

Read the appended [business overview and source reconciliation](../Product%20Definition.md#business-overview--current-direction-8-november-2026) for customer responsibilities, interactions and the distinction between v0.1 and the broader Studio vision. The original product definition contains superseded decisions; its reconciliation table makes those differences explicit.
