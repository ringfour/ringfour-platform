# Apps pivot — active implementation plan

Governed by [specification](../../../app-pivot.md) and
[implementation backlog](../../../apps-pivot-backlog.md). Work sequentially.
The earlier 00–10 milestones describe legacy behavior and regression evidence.

## Current execution agreement · 24 November 2026

The user authorized completing AP-08, AP-09 and AP-10 before one combined human
acceptance pass. Continue sequential implementation after passing automated
component and real-protocol tests; pending operator confidence checks do not
block the next authorized milestone. Record them as pending, never passed.
Stack reloads/rebuilds remain operator-controlled. Resume the remaining
virtual/product/cutover milestones after the combined human pass.

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

## AP-06 — standalone Robot Check and publisher CLI (local preview acceptance complete)

- [x] Record explicit user authorization to continue before deployed AP-05 acceptance; keep hosted registry/Auth0 checks pending.
- [x] Create the independent `../robot-check` repository, plain entrypoint, typed `sample_count` configuration, pinned Docker/wheel dependencies and operator/author guide.
- [x] Package a versioned, checksum-pinned template archive with source commit/tag; CLI includes its pointer rather than a divergent code copy. Template v0.1.1 retains byte-identical image inputs from process-tested v0.1.0.
- [x] Add independent authoring/manifest/configuration validation and packaged schemas; distinguish editable repository input from finalized digest-pinned metadata.
- [x] Implement `app init|validate|build|publish`, clean explicit build inputs, source/image identity checks, frozen releases, deterministic API retries and prepared-manifest support; preserve legacy CLI workflows.
- [x] Pass 100 CLI tests, 5 example tests, 177 contracts and 5 OpenAPI checks, focused lint, template reproduction and standalone CLI wheel installation.
- [x] Run actual CLI/Buildx/Docker/Distribution/Kestrel/PostgreSQL publication acceptance: API failure after push, fresh image-store pull, unchanged v1 after distinct v2 and registry-down replay. Only disposable fixture engines/containers are operated.
- [x] Verify publication through real Auth0 and the user-selected local registry, followed by 20 live IMU samples with strictly advancing Agent stamps, completion and inspected exit 0 on 24 November. See [user evidence](../evidence/apps-user-acceptance.md). Separate real-principal/private-provider checks remain AP-05; managed runs remain AP-09.

Evidence: [AP-06](../evidence/AP-06.md). Human checks:
[AP-06](../human-testing/AP-06.md). Packaging contract:
[App authoring](../apps-authoring.md). No API/virtual reload is required by AP-06.
Runtime delivery, managed installation/execution and product UI remain later gates.

## AP-08 — owner-scoped engine prerequisite (isolated acceptance complete)

AP-07 requires AP-08, so the user-authorized installation work lands this prerequisite first.

- [x] Pin Engine REST v1.46/httpx, owner socket/network policy and actual engine target detection.
- [x] Persist create/start boundaries in SQLite 0017 and hold a lifetime enrollment/namespace writer fence.
- [x] Verify full ownership/image/network/ID before actions; retain exact-ID recovery, unknown outcomes and durable output before removal.
- [x] Keep App readiness/status independent of Runner; retain legacy mode and non-root/resource-limited workloads.
- [x] Pass 136 Daemon tests and actual registry/pull/create/start fault recovery with two replacement Daemon processes, stop/output/remove.
- [ ] Operator deploys the Daemon socket/GID/network topology; AP-08V and hosted AP-07/AP-09 dispatch are separate gates.

Evidence: [AP-08](../evidence/AP-08.md). Human checks: [AP-08](../human-testing/AP-08.md).
No live stack operation was performed; the test engines/stores were isolated.

## AP-07 — durable installation/configuration/idle updates (isolated acceptance complete)

- [x] Complete AP-08 first (`fee97f0`) and document the opt-in server/Daemon deployment gates.
- [x] Add durable install/configure/update/rollback/read APIs with scoped roles/imports, permanent retries, immutable full configurations and explicit expected generation.
- [x] Persist immutable command/dispatch evidence, negotiate installation support separately from inventory, and gate dispatch/replay by current session/namespace/credential.
- [x] Prepare with fresh Agent/engine compatibility and scoped grants, without container/run creation; atomically apply while idle and retain prior installed binding on failure.
- [x] Persist/replay operation-bound observation streams through API/Daemon replacement; enforce generation, terminal history and legacy start exclusion.
- [x] Pin canonical UTF-8 across C#/Node/CLI/Daemon with a Unicode fixture; retain schema and configuration byte limits.
- [x] Pass 148 API, 144 Daemon, 101 CLI, 179 contract and five OpenAPI checks, plus real CLI/Agent/Daemon/engine/registry/API recovery and two-App/update/rollback acceptance.
- [ ] Operator deploys API migration/feature flag and Daemon socket/GID/network overlay; perform real Auth0/provider and live sensor checks separately.

Evidence: [AP-07](../evidence/AP-07.md). Human checks: [AP-07](../human-testing/AP-07.md).
Install-family operations produce no run; hosted start/stop remains AP-09.

## Local registry/UI acceptance follow-up (implementation/isolated acceptance complete)

- [x] Record the user's selection of official Distribution plus Joxit UI in local Compose, replacing the tentative Docker Hub path.
- [x] Pin images, bind registry/UI to loopback, persist storage and use Joxit's same-origin proxy with Docker DNS resolution.
- [x] Add development-only internal API endpoint mapping without changing immutable image authority or production HTTPS/Auth0 policy.
- [x] Pass 157 API tests and real push/pull, Chromium catalog/tag/CORS, persistence and containerized API verification.
- [x] Operator starts/reloads local registry/UI/API and publishes a real Auth0 release; live IMU follows successfully. See [user evidence](../evidence/apps-user-acceptance.md).

[Evidence](../evidence/local-registry.md), [operator guide](../human-testing/local-registry.md).
No live stack operation was performed.

## AP-09 — hosted App runs (automated acceptance complete)

- [x] Implement Operator-authorized start/stop, immutable snapshots and permanent idempotency.
- [x] Serialize shared App/legacy/development exclusion; retain the slot during uncertain delivery.
- [x] Allow exact-run stop while start is pending; old stop never selects a newer run.
- [x] Negotiate run support separately from installation-only Daemons.
- [x] Recheck fresh Agent/engine and installed generation before create; preserve recovery/no relaunch.
- [x] Persist ordered run/operation observations and expose scoped run reads/lists.
- [x] Pass actual API/Postgres/Agent/Daemon/Engine start/replacement/stop acceptance.
- [ ] Combined human acceptance after AP-10 (additional confidence; not a development blocker).

Evidence: [AP-09](../evidence/AP-09.md). Human guide: [AP-09](../human-testing/AP-09.md).

## AP-10 — App runtime and durable evidence (automated acceptance complete)

- [x] Supply recoverable run-scoped credentials independently of Runner; persist hashes/expiry/revocation and reject terminal-run tokens.
- [x] Bind the existing Logbook/HITL/artifact runtime clients to actual owned App runs.
- [x] Snapshot bounded artifact bytes into a durable private spool and deliver after source mutation/deletion, API outage and Daemon replacement.
- [x] Deliver bounded output prefixes/sealing/truncation with exact offset replay, including after run completion.
- [x] Retain submitted versus applied decisions, timeout/cancellation distinctions and unknown engine state.
- [x] Add workspace-scoped App output/artifact/interaction/history routes and lifecycle events; preserve legacy readers and evidence.
- [x] Pass actual packaged runtime-client/API/Daemon/Engine acceptance without Runner, including offline spool recovery and human response across replacement.
- [ ] Combined operator/human confidence pass: [AP-08/09/10](../human-testing/AP-08-10.md).

Evidence: [AP-10](../evidence/AP-10.md). Human guide: [AP-10](../human-testing/AP-10.md).

## Subsequent work

AP-01 through AP-04 are accepted under the agreed local-wheel distribution.
AP-05 catalog/share/import and registry grants pass automated acceptance; real
local publication is proven, separate real-principal/provider checks remain
recorded. AP-06 publication and standalone live IMU acceptance are complete for
the selected local registry. AP-07/08/09/10 implementation and actual-protocol
acceptance are complete. Their combined live deployment/human pass is next,
as explicitly requested; pending human confidence checks did not block this
implementation sequence. No live stack operation was performed by the agent.

After that pass, resume virtual topology (AP-08V), Studio (AP-11) and operator CLI
(AP-12), converging at full two-user/two-robot AP-13 acceptance. Retire managed UI
(AP-14) and remaining Runner clients/defaults (AP-15) through their explicit drain
and recovery gates. AP-16 is a separate second-platform follow-up. Never record
unperformed real Auth0/provider/simulator/physical checks as passed. Operator
controls every Platform and virtual stack reload/rebuild.
