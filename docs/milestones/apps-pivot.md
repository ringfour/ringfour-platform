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

## AP-04 — independent local SDK/runtime wheels (acceptance complete)

- [x] Record the user's `uv`/local-wheel distribution decision in the pivot specification and backlog; public Python publication is deferred.
- [x] Pin source commits, build backends/constraints, epochs, package and dependency wheel checksums, Python/uv images and per-target manifests.
- [x] Build both independent packages from clean Git snapshots into a versioned bundle; reject changed hashes and existing output destinations.
- [x] Replace the optional SDK image's Runner vendor dependency with an offline local-bundle consumer; retain the legacy wheelhouse and Agent dependency pin.
- [x] Correct SDK/Agent Platform integration paths and document optional runtime clients without claiming AP-10 delivery is implemented.
- [x] Verify SDK-only, SDK with optional runtime, and runtime without SDK in independent offline containers on linux/amd64; bundle and verify arm64 dependency wheels without claiming arm64 execution.
- [x] Verify a valid-ZIP tampered wheel is refused by its original hash lock, repeat builds produce identical bundle checksums, and overwriting output is refused.
- [x] Restore and pin the external conformance repository: 708 passed, 2 explicitly skipped; optional runtime tests: 9 passed.

Evidence: [AP-04](../evidence/AP-04.md). Human checks:
[AP-04](../human-testing/AP-04.md). Packaging contract:
[Python distribution](../python-distribution.md). Bundle location:
`/home/cdr/.local/share/ringfour/python-artifacts/0.1.0.dev0/`.
No Platform/virtual stack reload is needed for this slice.

## AP-05 — verified catalog, shares/imports and pull grants (implementation and isolated acceptance complete)

- [x] Implement authenticated publication, Store/App/revision reads and scoped pagination using AP-01 contracts and AP-02 tables.
- [x] Validate manifest semantics/canonical digests and actual registry manifest/config bytes before publication; fail closed for unconfigured hosts/prefixes, indexes, mismatches, redirects and dependency failure.
- [x] Serialize natural publication/import uniqueness and permanent idempotency outcomes; recheck mutation roles after locking and retain original replay/provenance.
- [x] Grant/revoke destination-bound shares and preserve committed imports after expiry/revoke without exposing consumer resources to the publisher.
- [x] Define and implement machine registry grants bound to negotiated session, current operation/candidate/import, exact image and deadline; request short-lived pull-only scope, recheck credential revocation after exchange, and keep credentials out of durable records and bind private repository policy to the original publisher.
- [x] Pass full API regressions, contract/security fixtures and isolated Kestrel/PostgreSQL/real Distribution acceptance with private token refresh and API restart replay during registry outage.
- [x] Add fail-closed server registry configuration and a human guide; retain installation/run dispatch for AP-07+.
- [x] Operator reloads the stacks during AP-06; API readiness 200 and anonymous Store 401 confirm the route is deployed.
- [ ] Configure a hosted registry and verify publication/import with separate real Auth0 principals/workspaces and the selected provider's private pull policy.

Evidence: [AP-05](../evidence/AP-05.md). Human checks:
[AP-05](../human-testing/AP-05.md). No new EF migration and no virtual reload.
The process harness uses an isolated restricted Bearer gateway with real
Distribution, not a hosted provider or real Auth0 credentials. The user explicitly
authorized AP-06 component work before testing these external gates.

## AP-06 — standalone Robot Check and publisher CLI (component/isolated acceptance complete)

- [x] Record explicit user authorization to continue before deployed AP-05 acceptance; keep hosted registry/Auth0 checks pending.
- [x] Create the independent `../robot-check` repository, plain entrypoint, typed `sample_count` configuration, pinned Docker/wheel dependencies and operator/author guide.
- [x] Package a versioned, checksum-pinned template archive with source commit/tag; CLI includes its pointer rather than a divergent code copy. Template v0.1.1 retains byte-identical image inputs from process-tested v0.1.0.
- [x] Add independent authoring/manifest/configuration validation and packaged schemas; distinguish editable repository input from finalized digest-pinned metadata.
- [x] Implement `app init|validate|build|publish`, clean explicit build inputs, source/image identity checks, frozen releases, deterministic API retries and prepared-manifest support; preserve legacy CLI workflows.
- [x] Pass 100 CLI tests, 5 example tests, 177 contracts and 5 OpenAPI checks, focused lint, template reproduction and standalone CLI wheel installation.
- [x] Run actual CLI/Buildx/Docker/Distribution/Kestrel/PostgreSQL publication acceptance: API failure after push, fresh image-store pull, unchanged v1 after distinct v2 and registry-down replay. Only disposable fixture engines/containers are operated.
- [ ] Verify hosted publication through real Auth0/registry policy and positive live simulator IMU output. Deployed Agent was reachable but sampling timed out with no running Base101 simulator; no live success is claimed.

Evidence: [AP-06](../evidence/AP-06.md). Human checks:
[AP-06](../human-testing/AP-06.md). Packaging contract:
[App authoring](../apps-authoring.md). No API/virtual reload is required by AP-06.
Runtime delivery, managed installation/execution and product UI remain later gates.

## Subsequent work

AP-01 through AP-04 acceptance are complete. AP-04 uses the agreed local
wheel distribution; public Python publication is deferred. AP-05 Store/share/import and registry grants pass isolated acceptance; operator
deployment is confirmed, while hosted registry/Auth0 checks remain pending. AP-06 authoring and publication CLI workflows pass component/isolated acceptance;
hosted publication and live sensor checks remain pending. Owner-scoped engine management (AP-08) precedes install
(AP-07) and mutually exclusive runs (AP-09). Runtime evidence (AP-10), Studio
(AP-11), CLI (AP-12) and virtual topology (AP-08V) converge at AP-13 acceptance.
Retire managed UI (AP-14) and remaining Runner clients/defaults (AP-15) only
through their explicit drain and recovery gates. AP-16 is a separate follow-up.

AP-01 defines the contracts and supplies independently tested readers; its acceptance does not claim live App execution. AP-02 persistence/reset is accepted. AP-03 negotiation and inventory routes are accepted, including deployed `virt2` verification; App installation/run dispatch remains disabled.

AP-05 publication/read/import routes are deployed; hosted registry/Auth0 acceptance remains pending.
App installation/run dispatch is not enabled by these slices. Do not
mark an external registry, Auth0, simulator or physical gate as passing from
schema fixtures. The operator performs every Platform/virtual stack operation.
