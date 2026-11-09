# 00 — Foundation

## Scope and prerequisites

None. Bootable infrastructure only; no product authentication or execution.

Complete prerequisite milestone none before beginning. Shared schemas, authorization, errors, limits, idempotency and durability rules in [architecture](../architecture.md) apply.

## Contracts

Define GET /health/live and /health/ready for API, daemon and Runner. Liveness checks process health; readiness checks owned dependencies (API PostgreSQL, daemon SQLite/Runner, Runner writable volume/uv). Health responses expose status/version only; no product data. Define a versioned health schema.

Freeze request/response/message schemas, state transitions and error examples in packages/contracts before implementation. New interfaces here are proposed Ringfour contracts, not descriptions of existing SDK interfaces.

## Implementation

Create independent app packages and lockfiles, initial Drizzle/SQLite migrations, local Compose networking/volumes and private Runner service. CLI `ringfour health` checks configured services. Console shows health only in development. Establish documented commands for install, lint, type checks, component tests, integration, migration and clean startup. No Docker socket mounts.

## Independent component tests

API readiness fails with unavailable PostgreSQL; daemon migrations are repeatable; Runner detects unwritable storage; CLI returns nonzero on failed readiness; Console handles unreachable services.

Run each affected application's tests independently; use fixtures for external dependencies only at this tier. Unchanged applications need regression coverage, not invented feature work.

## Integrated acceptance

From empty volumes start real services, migrate, invoke CLI health and inspect Console. Stop PostgreSQL and Runner independently and observe correct readiness failures; restore and recover without deleting volumes.

Exercise real processes and protocols. Record exact commands, expected and observed results, versions and cleanup steps in docs/evidence/00.md. A mock-only result does not satisfy acceptance.

## Definition of done

All services boot reproducibly; component and integration suites pass; repository layout, commands and environment example are documented without secrets.

Contracts and generated consumers agree; migrations work from empty and previous milestone state; relevant regressions pass; no secrets in evidence. Review and commit this slice before proceeding. Unavailable external prerequisites remain visible incomplete gates.
