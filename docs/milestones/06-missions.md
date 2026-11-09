# 06 — Missions

## Scope and prerequisites

05. Immutable revision deployment and lifecycle supervision; no scheduler or multi-robot orchestration.

Complete prerequisite milestone 05 before beginning. Shared schemas, authorization, errors, limits, idempotency and durability rules in [architecture](../architecture.md) apply.

## Contracts

Mission manifest schema v1: name, entrypoint, Python constraint, lock file, source digest and optional external robot-profile reference; validate entrypoint/lock paths. POST /v1/missions, /v1/missions/{id}/revisions, /v1/robots/{id}/deployments; POST /v1/deployments/{id}/{start|stop|restart}; GET deployment status. Track desired stopped/running, observed state, active revision and operation ID. Default no auto-restart; explicit bounded on-failure policy only if implemented and tested.

Freeze request/response/message schemas, state transitions and error examples in packages/contracts before implementation. New interfaces here are proposed Ringfour contracts, not descriptions of existing SDK interfaces.

## Implementation

CLI `mission publish`, `deploy`, `mission start/stop/restart/status`. Daemon prepares candidate revision before atomic activation; failed preparation leaves prior revision selected. Reject replacing an actively running revision until explicit stop; restart creates a new execution ID for the selected revision. Persist desired state; reconcile without replaying uncertain executions. Console inspection optional.

## Independent component tests

Manifest validation and immutability; version conflicts; prepare-before-activate; start/stop/restart idempotency; daemon crash during activation; cache GC preserves active revision.

Run each affected application's tests independently; use fixtures for external dependencies only at this tier. Unchanged applications need regression coverage, not invented feature work.

## Integrated acceptance

Publish A, deploy/start/stop/restart and inspect revision/execution attribution. Publish invalid B and prove A remains selected. Restart services and reconcile state. Attempt parallel script/mission and observe conflict, not two active processes.

Exercise real processes and protocols. Record exact commands, expected and observed results, versions and cleanup steps in docs/evidence/06.md. A mock-only result does not satisfy acceptance.

## Definition of done

Versioned mission lifecycle works with stable retry behavior; failed update preserves selected revision; supervision and restart policy documented and tested.

Contracts and generated consumers agree; migrations work from empty and previous milestone state; relevant regressions pass; no secrets in evidence. Review and commit this slice before proceeding. Unavailable external prerequisites remain visible incomplete gates.
