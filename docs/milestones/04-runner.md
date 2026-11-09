# 04 — Runner

## Scope and prerequisites

03. Fixed Runner and local Python execution; no user upload pipeline yet.

Complete prerequisite milestone 03 before beginning. Shared schemas, authorization, errors, limits, idempotency and durability rules in [architecture](../architecture.md) apply.

## Contracts

Private Runner API: POST /v1/executions with execution_id, revision digest, prepared code reference and entrypoint; GET /v1/executions/{id}; POST /v1/executions/{id}/stop; GET output with stream/offset. Stable ID deduplicates start; different payload conflicts. Use authenticated daemon requests, private networking and shared read-only staged bundles.

Freeze request/response/message schemas, state transitions and error examples in packages/contracts before implementation. New interfaces here are proposed Ringfour contracts, not descriptions of existing SDK interfaces.

## Implementation

Runner validates bundle, prepares uv environment and launches process group, captures separate stdout/stderr, records status and stop result. Daemon coordinates execution through its durable operation record; CLI exposes a development diagnostic fixture execution via hosted operation dispatch. Add early Balena manifests with separate daemon/Runner volumes/network, no socket and no Console.

## Independent component tests

Runner tests preparation failure, traversal rejection, duplicate start, nonzero exit, output limits, stop escalation, overlap conflict and restart reconciliation. Daemon tests mapping statuses and replay without duplicate launch.

Run each affected application's tests independently; use fixtures for external dependencies only at this tier. Unchanged applications need regression coverage, not invented feature work.

## Integrated acceptance

Real API→daemon→Runner executes a pinned hello fixture, captures output/exit and stops a long fixture. Restart daemon and Runner separately. On a real Balena device verify private reachability, volume persistence, uv preparation and execution; record versions and evidence.

Exercise real processes and protocols. Record exact commands, expected and observed results, versions and cleanup steps in docs/evidence/04.md. A mock-only result does not satisfy acceptance.

## Definition of done

Local execution and early physical Balena smoke both pass; Runner is fixed/non-root and no Docker socket is mounted; interrupted work has an explicit reconciled result.

Contracts and generated consumers agree; migrations work from empty and previous milestone state; relevant regressions pass; no secrets in evidence. Review and commit this slice before proceeding. Unavailable external prerequisites remain visible incomplete gates.
