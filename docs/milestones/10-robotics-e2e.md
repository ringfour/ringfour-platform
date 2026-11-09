# 10 — Balena / ROS SDK–Agent E2E

## Scope and prerequisites

09 and access to the actual SDK repository, Agent/robot repository, Balena device and ROS-backed capability.

Complete prerequisite milestone 09 before beginning. Shared schemas, authorization, errors, limits, idempotency and durability rules in [architecture](../architecture.md) apply.

## Contracts

Inspect and pin real SDK/Agent versions and their documented connection/authentication/capability interfaces. Record exact verified calls, profile owner/path/version and a supported read-only ROS-backed capability in docs/evidence/10.md. Ringfour manifest may reference that profile/version but does not define it. No speculative SDK methods or replacement ROS bridge.

Freeze request/response/message schemas, state transitions and error examples in packages/contracts before implementation. New interfaces here are proposed Ringfour contracts, not descriptions of existing SDK interfaces.

## Implementation

Finalize Balena service definitions for daemon, fixed Runner and existing Agent/ROS services per their actual deployment guidance. Inject supported Agent connection configuration/secrets securely into mission environment. Package pinned SDK through uv lock. Write smallest mission using a verified read-only SDK call, record result as output/artifact; optional second fixture requests HITL after observation.

## Independent component tests

Test Ringfour configuration validation, missing credentials, unavailable Agent and incompatible profile/version reporting. Use doubles only for component tests; validate manifest mounts, no Docker socket, private Runner reachability and dependency locks.

Run each affected application's tests independently; use fixtures for external dependencies only at this tier. Unchanged applications need regression coverage, not invented feature work.

## Integrated acceptance

On actual Balena/ROS device: Auth0 login → org → enroll → online → publish/deploy/start mission → verified SDK call → existing Agent → real ROS-backed reading → output/artifact/Logbook. Stop and restart mission, interrupt platform connectivity and restore, restart services and inspect reconciliation. Verify no duplicate launch and no robot motion required. Record versions, commands, sanitized logs and observed reading provenance.

Exercise real processes and protocols. Record exact commands, expected and observed results, versions and cleanup steps in docs/evidence/10.md. A mock-only result does not satisfy acceptance.

## Definition of done

Actual hardware E2E and earlier regression gates pass. SDK/Agent/profile remain externally owned and pinned; all evidence is reproducible. Missing device/repo/credentials blocks this gate explicitly; simulated results cannot complete it.

Contracts and generated consumers agree; migrations work from empty and previous milestone state; relevant regressions pass; no secrets in evidence. Review and commit this slice before proceeding. Unavailable external prerequisites remain visible incomplete gates.
