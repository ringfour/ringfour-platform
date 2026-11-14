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
| Execution | Fixed, separate Python Runner container; `uv`; cached per-revision virtual environments; no Docker socket |
| Developer tool | Python Typer + Rich CLI |
| Console | Internal development-only React + TypeScript + Vite + shadcn/ui; authenticated, deliberate privileges |
| Repository | Monorepo now; independent application dependencies, packaging and release boundaries for later extraction |
| Robotics | Existing SDK and Agent stay separate projects; Agent owns ROS, capabilities and safety |
| Robot profile | Owned by Agent/robot repository; Ringfour references its identity/version, never becomes its authoring authority |

Infrastructure such as Balena owns service containers. Ringfour provisions Python application code inside the fixed Runner. v0.1 runs trusted customer-authored code. A virtual environment isolates dependencies, not hostile code. Studio, third-party mission distribution, WhatsApp integration and generalized execution backends are deferred.

## Repository

```text
apps/api/                            # .NET solution, API project and xUnit tests
  src/Ringfour.Api/                  # business modules and EF Core migrations
  tests/Ringfour.Api.Tests/          # unit and Testcontainers coverage
apps/{daemon,runner,cli,console}/     # independently packaged applications
packages/contracts/                  # OpenAPI + JSON Schema and fixtures
infra/{compose,balena}/
tests/e2e/
docs/architecture.md
docs/milestones/
AGENTS.md
```

Applications communicate through versioned contracts, never imports of another application's implementation. The Ringfour SDK and Agent are independent open-source projects, pinned for integration and not copied into this tree. The CLI and internal Console use the same documented Platform API; its implementation language does not affect their contracts.

## Reading and execution order

Use the [starter database schema](docs/database-schema.md) for milestones 00–03 and its change rules for later persistence work.

Read [AGENTS.md](AGENTS.md), then [architecture](docs/architecture.md). Complete these milestones in order:

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
