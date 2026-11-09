# 02 — Enrollment

## Scope and prerequisites

01. Robot identity and machine credentials; no remote execution.

Complete prerequisite milestone 01 before beginning. Shared schemas, authorization, errors, limits, idempotency and durability rules in [architecture](../architecture.md) apply.

## Contracts

Apply the [starter database schema](../database-schema.md) for milestone 02.

POST /v1/organizations/{org_id}/robots; GET list/detail; POST /v1/robots/{id}/enrollment-tokens (owner only); POST /v1/device/enroll consumes one short-lived single-use token and returns robot-bound credential once. POST credential rotation/revocation under /v1/robots/{id}/credentials. Bind token to robot/org; persist expiration and consumption atomically.

Freeze request/response/message schemas, state transitions and error examples in packages/contracts before implementation. New interfaces here are proposed Ringfour contracts, not descriptions of existing SDK interfaces.

## Implementation

CLI `robot create/list/show`, `robot enrollment-token`, local `daemon enroll` and credential revoke/rotate commands. Daemon stores credential with restrictive permissions and enrollment identity in SQLite. API hashes enrollment secrets, records audit events, and exposes status without secrets. Optional Console robot inspector. Robot profile reference is optional external metadata owned by Agent/robot repo.

## Independent component tests

Concurrent token consumption has one winner; expired/reused/wrong-scope tokens fail; credential rotation/revocation and secret redaction work; daemon enrollment survives restart.

Run each affected application's tests independently; use fixtures for external dependencies only at this tier. Unchanged applications need regression coverage, not invented feature work.

## Integrated acceptance

Authenticated owner creates robot, enrolls actual daemon through CLI, restarts it and observes identity. Reuse token and try another org. Rotate with documented overlap policy, revoke old credentials and prove rejection.

Exercise real processes and protocols. Record exact commands, expected and observed results, versions and cleanup steps in docs/evidence/02.md. A mock-only result does not satisfy acceptance.

## Definition of done

One robot/org binding is durable; no reusable enrollment secret; CLI/API/daemon tests and integrated negative paths pass.

Contracts and generated consumers agree; migrations work from empty and previous milestone state; relevant regressions pass; no secrets in evidence. Review and commit this slice before proceeding. Unavailable external prerequisites remain visible incomplete gates.
