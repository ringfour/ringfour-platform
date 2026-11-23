# Apps pivot — active implementation plan

Governed by [specification](../../../app-pivot.md) and
[implementation backlog](../../../apps-pivot-backlog.md). Work sequentially.
The earlier 00–10 milestones describe legacy behavior and regression evidence.

## AP-01 — contracts and authority (contract acceptance complete)

- [x] Resolve socket and deployment authority in normative instructions.
- [x] Define bounded manifest/configuration subset and semantic validation rules.
- [x] Add Robot Check manifest, canonical digest and rejected fixtures.
- [x] Restore strict identity email/URI format validation and regression tests.
- [x] Define API/runtime schemas for App/revision/import/install/config/run,
  operation transitions, actor/timestamp provenance, grants and errors.
- [x] Define negotiated device support, target/current inventory, stale/offline
  observations and all affected typed consumers; preserve legacy protocol.
- [x] Cover direct shares, early acknowledgement, offline observations, lost
  containers and delayed stop retries in cross-language fixtures.
- [x] Verify all OpenAPI/schema reference targets, new route authentication and required App retry headers.
- [x] Finish negotiated-device and cross-language AP-01 acceptance before AP-02.

Evidence: [AP-01](../evidence/AP-01.md). Human checks:
[AP-01](../human-testing/AP-01.md). Contract-only changes require no stack reload.

## AP-02 — reset/bootstrap and identity preservation (acceptance complete)

- [x] Confirm the checked-in EF chain initializes the current Platform schema from empty PostgreSQL; no reset-only migration is needed.
- [x] Add a versioned identity-only export/restore utility for users, organizations, and memberships, preserving database IDs and Auth0 subject mappings.
- [x] Add the Apps EF persistence entities, relational constraints, model snapshot, and `AppsPersistence` migration.
- [x] Add a PostgreSQL execution-slot table and triggers covering active legacy and App runs.
- [x] Enforce one pending operation per installation and workspace/robot-safe resource relationships.
- [x] Add an additive Daemon SQLite migration for durable deployment namespace, App installation/run state, credentials by hash, and sequenced observation outbox; keep existing enrollment and pending legacy records intact.
- [x] Rehearse fresh PostgreSQL initialization and identity-only export/restore on disposable PostgreSQL 18; repeat EF migration and confirm target stays identity-only.
- [x] Rehearse the additive device SQLite migration from v14 and from empty; repeat safely and confirm identity/pending operation preservation.
- [x] Perform the user-authorized offline reset: preserve 3 users, 2 organizations and 4 memberships in JSON, restore into a fresh database, empty 37 application tables and delete object storage. Full-backup/recovery gates explicitly waived.
- [x] Verify original identity/organization IDs, roles and owner authorization after operator startup: real Auth0 `/v1/me` 200, owner-only reads 200 for both organizations, unauthenticated reads 401.

The stopped Platform database was replaced with the verified fresh cluster and
its filesystem object storage was emptied under explicit user authorization.
The operator started Platform and confirmed browser login. Real Auth0 checks
resolved the original user UUID and roles and passed owner access for both
organizations. At AP-02 acceptance, API readiness was 200 and all 35 migrations were
applied. Virtual subsequently re-enrolled as `virt2` for AP-03 below.
See [evidence](../evidence/AP-02.md) and [human guide](../human-testing/AP-02.md).

## AP-03 — Agent inventory and compatibility (acceptance complete)

- [x] Read bounded, fresh Agent hello over the existing protocol; preserve profile/capability/instance provenance without sensor or motion calls.
- [x] Supply engine OS/architecture from virtual infrastructure and enforce hard/optional/arm/native/image requirements on a fresh device observation.
- [x] Negotiate App protocol per current session and stable namespace; persist inventory projection, exact report deduplication and original receipt times.
- [x] Fence old/replaced sessions and enforce robot/workspace/revision read authority independently.
- [x] Add scoped status and compatibility endpoints; retain offline and last-successful observations as explanatory data.
- [x] Run independent fixtures/regressions and real Agent/Daemon/Kestrel/PostgreSQL process acceptance, including restart, Agent outage and offline retention.
- [x] Operator reloads Platform API and virtual Daemon and enrolls `virt2`; live authenticated status is online/supported, with the ten Base101 capabilities and base camera, engine-matching platform, stable namespace and advancing reports. All 36 PostgreSQL migrations and SQLite schema 16 are applied. No stack operation is performed by the agent.

Evidence: [AP-03](../evidence/AP-03.md). Operator steps:
[AP-03](../human-testing/AP-03.md). The ROS-free acceptance proves advertised
inventory and transport/recovery behavior; it does not prove sensor freshness or
simulator/physical operation. Pre-pull/start refusal with zero engine effects is
retained for AP-13 after those execution paths exist.

## Subsequent work

AP-01, AP-02 and AP-03 acceptance are complete.
AP-03 inventory and AP-04 released libraries precede distribution and
publishing (AP-05/06). Owner-scoped engine management (AP-08) precedes install
(AP-07) and mutually exclusive runs (AP-09). Runtime evidence (AP-10), Studio
(AP-11), CLI (AP-12) and virtual topology (AP-08V) converge at AP-13 acceptance.
Retire managed UI (AP-14) and remaining Runner clients/defaults (AP-15) only
through their explicit drain and recovery gates. AP-16 is a separate follow-up.

AP-01 defines the contracts and supplies independently tested readers; its acceptance does not claim live App execution. AP-02 persistence/reset is accepted. AP-03 negotiation and inventory routes are accepted, including deployed `virt2` verification; App installation/run dispatch remains disabled.

No App publication/install/run endpoints or dispatch are enabled by these slices. Do not
mark an external registry, Auth0, simulator or physical gate as passing from
schema fixtures. The operator performs every Platform/virtual stack operation.
