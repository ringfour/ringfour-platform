# 05 — Scripts

## Scope and prerequisites

04. Ad hoc uploaded Python execution, logs and artifacts; no mission lifecycle.

Complete prerequisite milestone 04 before beginning. Shared schemas, authorization, errors, limits, idempotency and durability rules in [architecture](../architecture.md) apply.

## Contracts

POST /v1/script-revisions initiates immutable source upload with digest, entrypoint and dependency lock; finalize endpoint verifies stored bytes. POST /v1/robots/{id}/script-runs returns 202 with operation/execution IDs. GET /v1/executions/{id}, /output and /artifacts plus authenticated artifact download. Define upload limits, output pagination, cancellation and retention.

Freeze request/response/message schemas, state transitions and error examples in packages/contracts before implementation. New interfaces here are proposed Ringfour contracts, not descriptions of existing SDK interfaces.

## Implementation

Implement private artifact storage adapter and local filesystem backend; configure hosted backend. CLI `run hello.py --robot ID`, `logs`, `execution show/stop`, `artifact download`. Upload bundle/lock, dispatch stable operation, stage/download/verify at daemon, execute via Runner. Return explicit unavailable error when disconnected rather than silently queuing runs. Capture author and basic durable events.

## Independent component tests

API authorization, upload finalization/idempotency/quotas; daemon digest mismatch and download interruption; Runner output offsets/artifact escaping; CLI output formatting and exit semantics (success 0, failure nonzero, interrupted/detached documented).

Run each affected application's tests independently; use fixtures for external dependencies only at this tier. Unchanged applications need regression coverage, not invented feature work.

## Integrated acceptance

CLI uploads and runs hello on real local stack; verify stdout, stderr, exit code, artifact bytes and actor. Run failing/cancelled script, repeat submission, deny other-org reads, interrupt upload and reconnect during output transfer.

Exercise real processes and protocols. Record exact commands, expected and observed results, versions and cleanup steps in docs/evidence/05.md. A mock-only result does not satisfy acceptance.

## Definition of done

One command produces a traceable result and downloadable artifact; retry preserves one execution; limits and retention documented; all component and integration gates pass.

Contracts and generated consumers agree; migrations work from empty and previous milestone state; relevant regressions pass; no secrets in evidence. Review and commit this slice before proceeding. Unavailable external prerequisites remain visible incomplete gates.
