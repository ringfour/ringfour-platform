# 09 — Human in the loop

## Scope and prerequisites

08. Durable structured human requests/responses; messaging adapters are deferred.

Complete prerequisite milestone 08 before beginning. Shared schemas, authorization, errors, limits, idempotency and durability rules in [architecture](../architecture.md) apply.

## Contracts

Define new Ringfour-local mission runtime HTTP contract (not an existing Robocore SDK API): create/get/wait interaction scoped by short-lived execution credential. Request fields: request_id, execution_id, prompt, finite choices, deadline, safe timeout outcome. States pending → answered | timed_out | cancelled. Hosted GET pending/detail and POST /v1/interactions/{id}/responses with chosen value and idempotency key. First authorized valid response wins atomically.

Freeze request/response/message schemas, state transitions and error examples in packages/contracts before implementation. New interfaces here are proposed Ringfour contracts, not descriptions of existing SDK interfaces.

## Implementation

Add thin Ringfour runtime client over the specified local protocol; document its new API separately from robot SDK. Daemon persists request before replication; API accepts operator responses, daemon applies once and resumes waiting code. Daemon owns authoritative deadline; API rejects expired submissions using deadline, daemon resolves any delayed response race. CLI `hitl list/show/respond`; Console can inspect/respond. Mission stop cancels pending requests. No automatic unsafe action on timeout.

## Independent component tests

Choice validation, caller/execution binding, unauthorized response, duplicate/concurrent answers, timeout/answer races, cancellation, restart and replay. Lost connection must leave mission waiting until durable answer or timeout, not fabricate approval.

Run each affected application's tests independently; use fixtures for external dependencies only at this tier. Unchanged applications need regression coverage, not invented feature work.

## Integrated acceptance

Mission requests choice; authorized CLI user answers and mission continues once with Logbook attribution. Restart daemon while pending; test offline timeout, late response and two competing operators. Other-org user cannot view/respond.

Exercise real processes and protocols. Record exact commands, expected and observed results, versions and cleanup steps in docs/evidence/09.md. A mock-only result does not satisfy acceptance.

## Definition of done

Requests survive restart; exactly one terminal outcome is applied; timeouts are explicit and recorded. Core acceptance passes without WhatsApp; optional adapter is a later independent feature.

Contracts and generated consumers agree; migrations work from empty and previous milestone state; relevant regressions pass; no secrets in evidence. Review and commit this slice before proceeding. Unavailable external prerequisites remain visible incomplete gates.
