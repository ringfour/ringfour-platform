# Apps pivot — active implementation plan

Governed by [specification](../../../app-pivot.md) and
[implementation backlog](../../../apps-pivot-backlog.md). Work sequentially.
The earlier 00–10 milestones describe legacy behavior and regression evidence.

## Current execution agreement · 25 November 2026

AP-14/AP-15 reductions and their combined acceptance are complete. AP-17's
settings/files scope and AP-17.F1 bounded floating-point support are
implemented. Contract, API, daemon, CLI, runtime and Console checks passed; the
full AP-17 process acceptance needs a rerun after a test harness fix for raw
NaN input. AP-19 API/Daemon performance work is live-accepted; broader
UI/browser acceptance remains deferred.
AP-19 backend wakeups and before/after benchmark are recorded below and in the
evidence file. Live stacks have not loaded the new wakeup code. The workspace
rule reserves all Platform and `ringfour-virtual` reload/rebuild operations for
the operator. Continue to distinguish isolated benchmark results from live
acceptance and leave browser/UI checks pending.

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

## AP-08V — opt-in App-capable virtual topology (implementation in progress)

- [x] Add a separate Runner-free `compose.apps.yaml`; retain legacy
  `compose.yaml` as the default migration path.
- [x] Add singleton and numbered launcher modes with external network names,
  Agent/Daemon aliases, host socket/GID wiring, engine platform reporting, and
  no Runner wheel staging or Platform-tree writes in App mode.
- [x] Add guarded down flow using the persisted Daemon owner identity and exact
  run-container labels; preserve `/data` and App evidence volumes by default.
- [x] Document topology and operator steps in the virtual README and
  [AP-08V guide](../human-testing/AP-08V.md).
- [x] Render the singleton and all 40 numbered Compose configurations; verify
  unique numbered ports, networks, aliases and Daemon volumes. Pass 10 mocked
  launcher/down safety checks.
- [ ] Run operator smoke for socket access, readiness, Daemon replacement,
  per-instance network/DNS isolation and state-preserving shutdown.

Implementation notes and unperformed checks: [AP-08V evidence](../evidence/AP-08V.md).

## Subsequent work

AP-01 through AP-04 are accepted under the agreed local-wheel distribution.
AP-05 catalog/share/import and registry grants pass automated acceptance; real
local publication is proven, separate real-principal/provider checks remain
recorded. AP-06 publication and standalone live IMU acceptance are complete for
the selected local registry. AP-07/08/09/10 implementation and actual-protocol
acceptance are complete. Their combined live deployment/human pass is next,
as explicitly requested; pending human confidence checks did not block this
implementation sequence. No live stack operation was performed by the agent.

AP-08V is in progress. Complete its implementation checks, then run the combined
AP-08/09/10/08V operator confidence pass before AP-13. The user explicitly
authorized AP-11 before that combined pass; they have accepted the current
browser smoke (views render and App import works). AP-12 follows before the
combined confidence pass. Those milestones converge at full
two-user/two-robot AP-13 acceptance. Retire managed UI (AP-14) and remaining
Runner clients/defaults (AP-15) through their explicit drain and recovery gates.
AP-16 is a separate second-platform follow-up. Never record unperformed real
Auth0/provider/simulator/physical checks as passed. Operator controls every
Platform and virtual stack reload/rebuild.

## AP-11 — authenticated Console Apps experience (implementation complete; smoke accepted)

- [x] Replace primary navigation with Home, Robots, Apps, App Store and Logbook.
- [x] Add a fresh Home view backed by workspace App runs, robot presence and
  inventory, operation history and pending HITL. Preserve exact run and robot
  links and the global timeline.
- [x] Replace robot detail's mission/deployment reads and controls with App
  status, inventory, installations and App runs.
- [x] Add listed Store discovery, release/capability detail, robot compatibility
  preview, direct-share lookup/import and install operation tracking.
- [x] Add workspace imported-App view with typed schema-driven configuration,
  revision update, start/stop, and per-App Logbook link.
- [x] Add App Logbook run/event filters, stdout/stderr, artifact download and
  robot links. Keep submitted HITL responses visible until device confirmation.
- [x] User browser smoke: primary views render and an App can be imported. The
  user accepted functional coverage at this stage and deferred visual polish.
- [ ] Full component coverage for stale/missing inventory, per-role controls,
  changed inventory, two-workspace cache isolation and operation refusal/failure.
- [ ] AP-13 multi-user/two-workspace browser acceptance. Keyboard/design-token
  polish is deferred and is not an AP-11 implementation blocker.

Implementation notes and the manual review checklist: [AP-11 guide](../human-testing/AP-11.md).

## AP-12 — App CLI operations (implementation underway)

- [x] Add Store search/show/import and robot-targeted install with optional
  direct-share ID and JSON configuration.
- [x] Add typed configure/update, explicit installation start and exact-run
  stop, with idempotency keys and operation tracking.
- [x] Add run/output/artifact and scoped App Logbook/HITL inspection, plus
  observed robot capabilities and optional compatibility assessment.
- [x] Add focused CLI tests for workspace-scoped install, App-to-installation
  resolution, ambiguity refusal, exact-run stop and rollback; all 4 pass.
- [x] Verify nested command help, Python compilation, Ruff and documented CLI
  command names.
- [ ] Complete clean CLI-to-API/Daemon real-protocol acceptance.
- [ ] Live Auth0/role-denial and multi-user acceptance remains part of AP-13.

Operator steps: [AP-12 guide](../human-testing/AP-12.md).
Automated evidence: [AP-12](../evidence/AP-12.md).
The live two-user workflow remains pending: [AP-13 guide](../human-testing/AP-13.md).

## AP-17 — App settings, Files and Studio (implementation complete; live artifact and browser follow-up open)

AP17.01 contract baseline is drafted and validated. It adds strict App manifest
v2 schemas while preserving v1, bounded environment mappings and reserved-name
rules, revisioned settings/application resources, negotiated device command and
observation shapes, local runtime routes, and workspace-scoped cross-run Files
pagination. The API/runtime OpenAPI operations are marked contract-only until
their implementations land. Design decisions and exact transitions are in
[AP17 contract decisions](../apps-ap17-contract.md); the project-root
[AP17 specification](../../../AP17.md) remains the acceptance source.

- [x] AP17.01: reconcile Platform, SDK, runtime package, App examples, design
  system, and virtual-stack ownership; freeze contracts, schemas, fixtures, and
  route authentication/error shapes. Preserve AP12/AP13 as separate gates.
- [x] AP17.02: production v2 manifest validation, literal startup environment
  mapping, negotiated feature persistence, and an additive migration are
  implemented. The Daemon advertises live settings when Apps are enabled; the
  API remains gated by `APPS_LIVE_SETTINGS_ENABLED`. The operator has reloaded
  the API and virtual Apps stack.
- [x] AP17.03: additive settings persistence, immutable revisions, API reads and
  writes, idempotency/CAS conflicts, and selected/application-requested
  Logbook events are implemented. Idle save, active exact-run candidate,
  schema rejection, stale selection, pending conflict, replay, and event
  visibility tests pass, including applied/rejected outcomes from device
  observations. Release update/rollback validate or explicitly reset settings;
  selection changes only after observed installation success.
- [x] AP17.04: the Daemon has a durable SQLite settings queue, run-scoped
  pending/result routes, exact-run result observations, and negotiated dispatch.
  Automated recovery coverage now checks a replacement runtime instance reading
  the same durable SQLite command, idempotent result retry, and isolation from a
  subsequent run. A separate test replays the unchanged observation report
  after API recovery. These exercise retained SQLite/outbox state;
  isolated API/Daemon process replacement and delayed-result integration passed
  in [the AP-17 process evidence](../evidence/ap17-process.json). A live virt2
  run also confirmed initial and updated settings.
- [x] AP17.05: the dependency-free `ringfour_runtime.settings` client and
  Robot Check v2 settings/summary-artifact source are implemented and covered
  by prior package/example checks. CLI v2 authoring validation was pulled
  forward as a prerequisite. Runtime `0.1.1.dev0` and Robot Check template
  `0.2.0` now have immutable source pins; the local wheel bundle and template
  archive are checksum verified. A clean amd64 example image built from the
  pinned inputs. Isolated integrated artifact publication passed. The live
  reference app exposed a relative-path mistake in its summary artifact call;
  fix and republish that example before claiming live artifact acceptance.
- [x] AP17.06: workspace-scoped cross-run App artifact listing now supports
  installation, robot, run, and path-prefix filters with filter-bound cursors,
  deterministic publication-time ordering, and a prospective receipt timestamp.
  Existing run-scoped downloads remain the content path. Integration coverage
  proves duplicate paths across runs, pagination, filter binding, and workspace
  isolation.
- [ ] AP17.07: App detail routes, installation selection, Home grouping, and
  Studio navigation are implemented. The Apps library now has searchable App
  rows with concise robot assignments, and Overview keeps its App identity,
  selected-target prompt, execution state and actions together. Home has one
  setup path for a workspace without robots and directs to Apps when an App is
  already imported, otherwise to App Store. Typecheck passes; browser,
  keyboard, narrow viewport, and stale-state acceptance remain.
- [ ] AP17.08: Studio startup/live settings, cross-run Files, and App Activity
  are wired to the hosted API. Pending/rejection display and verified downloads
  are implemented. Settings now separates next-start selection from current
  process confirmation, disables unresolved edits, and guards unsaved changes
  during navigation. Files now has synthesized folder browsing, breadcrumbs,
  and explicit incremental discovery labels. Activity uses readable event
  titles, expandable technical IDs, and distinct error/empty states. Browser,
  component and integrated API/device acceptance remain.
- [x] AP17.09: CLI settings show/set, aggregate artifact filters, and manifest
  v2/runtime/working-data documentation are implemented. Ruff and CLI help
  checks pass; the full CLI suite passes 67 tests. Install now sends nullable
  `share_id` explicitly, as required by the request schema.
- [x] AP17.10: automated integrated settings/files acceptance passed using the
  immutable runtime wheel and template pins, built image, live settings
  application/rejection, process replacement, verified artifact download, and
  cursor/filter isolation. Human browser checks and the reference example's
  live summary-artifact path correction remain separate follow-ups in
  [the AP-17 guide](../human-testing/AP-17.md).

Studio implementation is in progress: `/apps/{appId}/{overview|settings|files|activity}`
routes preserve workspace and optional installation selection. Overview groups
release, selected robot connectivity, compatibility, execution and actions;
Home groups active App assignments and deep-links each robot target, with a
single onboarding path for empty workspaces. Settings reads and writes the
existing revisioned API with pending-versus-confirmed state;
Files uses the cross-run listing and verifies downloaded bytes; Activity reads
the same App Logbook records. Console TypeScript checking and Biome on the
changed TSX files pass; browser/component acceptance remains pending. CLI now
has `app settings show/set` and cross-run `app artifacts --app` filters. Ruff
passes for the CLI entrypoint; its component tests and integrated acceptance
remain pending. AP17.07–AP17.10 stay unchecked until their full acceptance gates
and human guide are complete. AP17.05 pinned build evidence is recorded in
[`AP-17 evidence`](../evidence/AP-17.md).

AP17.01 checks: 191 Node tests, 5 OpenAPI checks, 9 API fixture tests, and 25
Daemon protocol tests passed. The full API suite passes 176 tests and the full
Daemon suite passes 162 tests. Runtime helper tests pass 14, CLI authoring
tests pass 21, and Robot Check tests pass 7. AP17 settings migrations were exercised on
disposable test databases. Source
reconciliation found the SDK is an independent Agent client, while the optional
runtime is Platform-owned; the Robot Check repository now includes simple
workflow examples; the virtual project owns Daemon deployment/topology only;
and `/home/cdr/ringfour/design_system` is the interface token source. These sibling repositories had existing local
changes and were inspected without editing them. The operator reloaded the
Platform API and virtual Apps stacks; no data volumes were recreated.

## AP-18 — Official Apps, updates and uninstall (server/CLI implemented; acceptance in progress)

AP18's API/Daemon/CLI operation layer is implemented. Store and installed
App UI controls are part of AP-21 by user direction. See
[the AP-18 human guide](../human-testing/AP-18.md) and
[current implementation evidence](../evidence/AP-18.md).

- [x] Restrict public catalog publication to the configured official publisher;
  default CLI publication to private and show the workspace/App/release/visibility.
- [x] Derive Ringfour publisher identity from trusted server configuration.
- [x] Add negotiated uninstall command, API operation, durable Daemon
  acknowledgement, soft removal and reinstall-safe identity.
- [x] Add AP-18 API migration and CLI uninstall operation.
- [x] Build the API; compile changed Python modules, pass CLI Ruff, and pass
  Console TypeScript typecheck.
- [x] Component checks: API 154, Daemon 114, CLI 69, contracts 179 and OpenAPI 5.
  Daemon checks cover uninstall resume/replay, stale binding and Start exclusion.
- [x] Disposable real-process acceptance: official discovery/import/install in
  a second workspace, CLI uninstall, reinstall identity, retained configuration,
  second-robot preservation and existing update/rollback/recovery checks.
- [ ] Configure the live official publisher workspace and apply both API migrations.
- [ ] Human acceptance of private/public publisher policy, update/rollback,
  uninstall/reinstall and replay recovery.
- [x] AP-21 wires official label, update/rollback and uninstall UI flows; live acceptance remains separately tracked.

The API and Daemon changes require the operator to reload the Platform and
`ringfour-virtual` stacks. No stack operation is performed by the agent.

## AP-19 — Responsive, consistent App state (backend implementation complete; broader acceptance in progress)

**Backend performance update · 25 November 2026:** added post-commit API and
Daemon wake signals while retaining the durable operation journal and periodic
recovery sweeps. In a 20-cycle isolated benchmark, command-dispatch p95 fell
from 1802 to 45 ms for start and 1992 to 22 ms for stop. Device-observed p95
fell from 3597 to 1558 ms for start and 2263 to 264 ms for stop. API projection
reads remained in the 3–7 ms p95 range at 100 catalog Apps; live read-only API
samples were 3–5 ms except `/v1/me` (~203 ms p95 due to Auth0 `/userinfo`).
Start remains slightly above the 1.5 s target in the nested-vfs fixture, where
container creation is the largest measured residual. Full results and limits
are in [AP-19 evidence](../evidence/AP-19.md). API and Daemon test suites pass
(150 and 110 tests); runtime and Robot Check pass (14 and 7).

The running stacks have not loaded these wakeup changes yet. Workspace
instructions reserve stack reload/rebuild operations for the operator. After
the operator reloads the Platform API and `ringfour-virtual`, rerun live
start/stop measurements. This does not complete the ten-robot UI, browser
flicker, or cross-view human acceptance gates below.

**Post-reload confirmation · 25 November 2026:** the operator reloaded both
stacks. Live reads were sampled again, and a real virt2 start/stop verified
34 ms start and 19 ms stop dispatch acknowledgements. Device-observed start
was 565 ms and stop 979 ms in this single cycle. The operator had the Console
open and confirmed the state updated live in place. The App process then
reported exit code 1 while publishing its summary because the reference
Robot Check template uses a relative artifact path; the operation itself
reached stopped successfully. This does not invalidate the API/Daemon wakeup
check, but it leaves that example's live artifact check open. The isolated
20-cycle benchmark's observed-start p95 is 1.56 s, narrowly above the 1.5 s
target; the ten-robot percentile and wider browser matrix remain unmeasured.

- [x] Implement and load commit-triggered API and Daemon wakeups while keeping
  durable operation state and periodic recovery. API/Daemon/runtime suites pass
  150/110/14; CLI passes 67. Isolated before/after benchmark and post-reload
  live lifecycle evidence are recorded in [AP-19 evidence](../evidence/AP-19.md).

**Implementation update · 25 November 2026:** bounded current/recent run reads,
in-place Console refresh, faster operation polling, AppRun timeline indexes,
immutable metadata conflict wording and terminal-operation projection refresh
have been implemented. App detail tabs now use client-side history navigation
instead of reloading the whole Console. Console build and AP-19 API migration succeeded. Direct
Docker Engine calls measured 4–44 ms; the prior ~11.5 s stop was a daemon lock
deadlock exposed by Docker's 10 s graceful-stop wait. Releasing the executor
and operation-handler mutexes before Docker waits reduced live stop to 0.833 s,
with exit code 0 and the summary artifact uploaded. A separate Console defect
left the App Overview stale after an operation reached terminal; a once-per-
operation callback now refreshes Apps and Robot detail. See measured evidence
in [AP-19 evidence](../evidence/AP-19.md).

The evidence does not yet establish why the general UI sometimes takes 3–5 s.
Warm API reads measured p95 2–5 ms except `/v1/me` at ~193 ms due to an Auth0
`/userinfo` call. Browser action-to-visible-state used a stable mocked Auth0
token and measured ~1.8 s, so real Auth0 token latency, rendering, database
contention and loaded behavior still require a correlated browser trace. The
live run discrepancy and visual refresh behavior need final real-browser
acceptance after the terminal refresh fix.

- [x] Capture single-run API/device timings, direct Docker request timings and
  a concrete stop deadlock trace. Browser render, SQL/lock profiling and
  loaded percentile baselines remain pending; see the evidence table.
- [ ] Capture a correlated latency baseline across browser, API, PostgreSQL,
  WebSocket command delivery, Daemon/engine, observation and render.
- [ ] Reproduce the run discrepancy for
  `66ac710b-cec1-460d-91be-8675436c9d98`; identify the authoritative state and
  repair/test its owning projection or refresh path.
- [x] Bound Home/Apps reads to current and recent data; remove per-record
  request growth and support recent-first cursor history with workspace/robot
  scoping and verified query plans.
- [x] Update views in place, protect against overlapping/stale refreshes, and
  refresh projections on terminal App operations.
- [ ] Confirm Home, Robots, Apps, Overview, Activity and Logbook agree on exact
  run IDs and observed-state freshness in a real-browser lifecycle acceptance.
- [x] Define and test source fingerprint, OCI image digest and manifest digest
  separately. Verify a display-name-only release reuses image bytes safely,
  updates listed metadata, and keeps immutable manifest/release conflict rules.
- [ ] Meet the AP-19 latency and request-scaling budgets in
  [the human acceptance guide](../human-testing/AP-19.md); save measurement and
  query-plan evidence in [AP-19 evidence](../evidence/AP-19.md).

Stack reload/rebuild remains operator-controlled by the workspace rule. If
implementation requires one, identify the affected stack and ask the operator
to reload it; do not perform that operation.

## AP-14/AP-15 reduction — 2026-11-25

The user approved [the full reduction inventory](../../../reduction.md): remove
non-App functionality, including active legacy commands/routes/workers. Legacy
data/callers need no migration; preserve identity and App state. This supersedes
earlier drain and legacy-reader gates in this plan. Human checks are combined
after implementation; see [the combined sweep](../human-testing/AP-14-15.md)
and [automated evidence](../evidence/AP-14-15.md).

- [x] Retire hosted Mission UI/Gradio and legacy CLI/API/daemon execution paths.
- [x] Remove Runner services, wheels, ports/config and default virtual topology.
- [x] Add identity-preserving PostgreSQL/SQLite cleanup migrations.
- [x] Update supported contracts, deployment scripts and current guides.
- [x] Pass integrated App installation/runtime/start-stop verification.
- [x] Operator reports Platform and virtual stacks restarted after reduction (2026-11-25).
- [ ] Record the combined cdr/ring1 human confidence sweep.

## AP-21 — Studio UI sweep (implemented; locally deployed; live acceptance pending)

AP21 covers route/navigation behavior and the owner journeys through Store,
installation management, App updates/rollback/uninstall, robot credentials,
Home and Logbook. It consumes AP18's server-enforced publication and durable
installation operations. See [AP-21 specification](../../../AP-21-studio-ui-sweep.md).

- [x] Reconcile the spec's baseline commit with the current Console.
- [x] Implement canonical navigation and retain browser Back/direct-entry behavior.
- [x] Wire official publisher labels and AP18 update/rollback/uninstall operations.
- [x] Replace Fleet Manage with enrollment/credentials routes; fix capabilities
  to read `inventory.current` and qualify last-known observations.
- [x] Implement Store/listing/picker, installation-aware membership, verified
  photo previews, grouped Home failures and App Activity; retain Logbook/Timeline.
- [x] Owner approved deployable Studio; add static production target, mandatory
  Auth0, and an initial local development target (subsequently switched to
  production during the frontend slowdown follow-up).
- [x] Browser fixture verifies desktop/narrow layouts, modal focus/Escape,
  exact targets, dirty link/Back/Forward guards and verified image preview.
- [x] Production static smoke verifies deep links, API proxy, unauthenticated
  denial and the sign-in screen.
- [ ] Operator reloads Platform, configures the official publisher, and completes
  live cdr/ring1 Auth0/robot/registry acceptance and hosted production login.

- [x] Adopt the owner's Lovable view structure at `1b99764`, retaining existing
  Ringfour CSS, Auth0/API actions, explicit installation scope and real results.
  Close layout copies cover shell, Home, Store, App tabs, robot access and Logbook;
  58 Console tests and 28 browser fixture screenshots validate the integration.
- [x] Apply the website-derived typography, graphite surfaces and lighter controls;
  expand shared App/listing headers with real descriptions and a separate
  robot/tabs toolbar. Verified across desktop/narrow fixture views.

- [x] Restore the pre-Apps Logbook presentation from `4204161` on current App
  runs/events/files; retain groups, filters, run record, duration/events and split
  output. Reapply the Organizations palette across Studio, enlarge the sidebar
  and use fluid desktop width. Latest checks: 65 Console tests and 34 page captures
  plus mobile/wide review; live acceptance remains separately tracked.

- [x] Apply the Apps header across every Studio view and add shell-owned top-bar
  back navigation with retained filters, workspace and unsaved-edit protection.
  Latest checks: 74 Console tests, 38 desktop/narrow captures plus drawer/wide,
  TypeScript/build/lint and production static smoke pass. Live gates stay open.
- [x] Move workspace selection, role and Organizations/health/profile into the
  top bar; reduce the sidebar to a 72px accessible icon rail. Latest: 75 Console
  tests, 38 desktop/phone captures plus tablet/drawer/wide and static smoke pass.

- [x] Implement `../uifix.md` with shared page/grid/section/history primitives,
  open App settings/overview/activity and robot sections, aligned Files filters,
  inventory truthfulness, variant/artwork handling and a layout cookbook.
  81 Console tests and populated desktop/phone feedback checks pass.
- [x] Diagnose whole-frontend slowdown, remove unused profile/artifact waits from
  Home/Robots, and deploy the compiled/compressed Console at the owner's request.
  Controlled Home/Robots navigation p50: ~350→150ms. Live authenticated timing
  and wider human acceptance remain open.

- [x] Modularize Studio by feature; separate auth/workspace/routing and major page
  controllers from presentation. Reuse collection/section/schema-form templates
  and document extension recipes. 85 Console tests, browser/static checks and
  unchanged navigation latency/request counts verify the refactor.

See [AP-21 evidence](../evidence/AP-21.md) and the
[human acceptance guide](../human-testing/AP-21.md). Browser fixtures are not
live authentication/device evidence.
