# 08 — Logbook

## Scope and prerequisites

07. Hosted durable searchable history, building on earlier event records.

Complete prerequisite milestone 07 before beginning. Shared schemas, authorization, errors, limits, idempotency and durability rules in [architecture](../architecture.md) apply.

## Contracts

Device event batches use event schema from architecture with acknowledged local sequence/checkpoint. GET /v1/organizations/{org_id}/events filters robot, execution, operation, actor, type and time with cursor pagination. GET execution history links revisions, lifecycle, output and artifacts. Events are append-only; corrections are new events.

Freeze request/response/message schemas, state transitions and error examples in packages/contracts before implementation. New interfaces here are proposed Ringfour contracts, not descriptions of existing SDK interfaces.

## Implementation

Transactional SQLite outbox with bounded disk and explicit backpressure; API idempotent ingest and organization-scoped PostgreSQL indexes. CLI `logbook list/show` and Console timeline. Include human action, machine action and originating actor separately; preserve execution/revision correlation and schema versions. Document retention, redaction and output truncation markers.

## Independent component tests

Atomic state/event commit, duplicate batches, lost acknowledgments, sequence gaps, disk pressure, pagination stability and cross-org filters. Test historical schema decoding and secrets redaction.

Run each affected application's tests independently; use fixtures for external dependencies only at this tier. Unchanged applications need regression coverage, not invented feature work.

## Integrated acceptance

Disconnect platform while a mission starts/stops locally; reconnect and reconstruct complete history once. Restart during ingest, replay batch and verify no duplicates. CLI/Console show same ordered per-device events and correct actors.

Exercise real processes and protocols. Record exact commands, expected and observed results, versions and cleanup steps in docs/evidence/08.md. A mock-only result does not satisfy acceptance.

## Definition of done

History survives service restarts/outage and identifies actor, revision and result; no silent event loss; retention/backpressure and all gates documented.

Contracts and generated consumers agree; migrations work from empty and previous milestone state; relevant regressions pass; no secrets in evidence. Review and commit this slice before proceeding. Unavailable external prerequisites remain visible incomplete gates.
