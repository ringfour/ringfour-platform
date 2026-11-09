# 07 — Development sessions

## Scope and prerequisites

06. Persistent mutable workspace and fast explicit restart; no remote shell or editor service.

Complete prerequisite milestone 06 before beginning. Shared schemas, authorization, errors, limits, idempotency and durability rules in [architecture](../architecture.md) apply.

## Contracts

POST /v1/robots/{id}/dev-sessions; GET session; PUT /v1/dev-sessions/{id}/source with base generation and file manifest; POST restart; DELETE session. Define session ID, owner, workspace generation, dependency digest and expiry. Reject stale generations with 409. Exclude secrets, .git and venv from sync; deletion list is explicit.

Freeze request/response/message schemas, state transitions and error examples in packages/contracts before implementation. New interfaces here are proposed Ringfour contracts, not descriptions of existing SDK interfaces.

## Implementation

CLI `dev start/sync/restart/stop` and optional watch flag. Persist session workspace and venv in Runner volume. Stage a complete generation before swapping code; source-only restart reuses venv, dependency changes require prepare then activation. One active execution rule applies; owner/privileged users can end session. Expiry stops execution and schedules bounded cleanup; it does not create a production revision.

## Independent component tests

Safe paths, deletions, ignore rules, stale updates, partial transfer, authorization, dependency fingerprint and workspace persistence. CLI watch debounce and reconnect retry; Runner cleanup cannot remove active production data.

Run each affected application's tests independently; use fixtures for external dependencies only at this tier. Unchanged applications need regression coverage, not invented feature work.

## Integrated acceptance

Start session, edit/sync/restart and observe new output without dependency installation. Restart daemon/Runner and recover workspace; change lock and verify reprovision. Conflicting sync is rejected; terminate/expire session and verify resources released.

Exercise real processes and protocols. Record exact commands, expected and observed results, versions and cleanup steps in docs/evidence/07.md. A mock-only result does not satisfy acceptance.

## Definition of done

Code iteration is reproducible, workspace survives restart, no partial generation runs, no implicit production mutation; component and integration gates pass.

Contracts and generated consumers agree; migrations work from empty and previous milestone state; relevant regressions pass; no secrets in evidence. Review and commit this slice before proceeding. Unavailable external prerequisites remain visible incomplete gates.
