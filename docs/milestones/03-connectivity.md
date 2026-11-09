# 03 — Connectivity

## Scope and prerequisites

02. Durable outbound connection and status; command envelope only, no code execution.

Complete prerequisite milestone 02 before beginning. Shared schemas, authorization, errors, limits, idempotency and durability rules in [architecture](../architecture.md) apply.

## Contracts

Implement architecture WebSocket protocol with hello, heartbeat, command, ack and operation_status messages. Hello includes supported protocol and daemon version; API returns server time and negotiated heartbeat. GET /v1/robots/{id}/status returns last_seen, online/offline and observed version. Add authenticated protocol diagnostic no-op operation for tests.

Freeze request/response/message schemas, state transitions and error examples in packages/contracts before implementation. New interfaces here are proposed Ringfour contracts, not descriptions of existing SDK interfaces.

## Implementation

API tracks live sessions and durable command status; reject competing sessions using an explicit replacement policy. Daemon reconnects, persists/deduplicates no-op commands and reconciles status. CLI `robot status` and Console status inspector. Revocation closes active sessions and denies reconnect.

## Independent component tests

Validate schemas, stale/duplicate sequences, retry backoff and status timeout using controlled clocks. Verify dedup across restart, revoked credentials and unsupported version errors.

Run each affected application's tests independently; use fixtures for external dependencies only at this tier. Unchanged applications need regression coverage, not invented feature work.

## Integrated acceptance

Connect real daemon/API; send diagnostic operation, interrupt network before ack, restart and reconnect. One durable effect/result appears; offline after threshold and online after reconnect; revocation disconnects device.

Exercise real processes and protocols. Record exact commands, expected and observed results, versions and cleanup steps in docs/evidence/03.md. A mock-only result does not satisfy acceptance.

## Definition of done

Status is based on server receive time; retries do not duplicate effects; no unbounded queue; reconnect/restart gates pass.

Contracts and generated consumers agree; migrations work from empty and previous milestone state; relevant regressions pass; no secrets in evidence. Review and commit this slice before proceeding. Unavailable external prerequisites remain visible incomplete gates.
