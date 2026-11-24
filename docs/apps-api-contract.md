# Apps API and runtime v1 — new Ringfour design

Status: AP-01/02 contracts/persistence, AP-03 inventory, AP-04 local wheel
packaging and AP-05 catalog/grants are implemented. AP-06 supplies authoring;
AP-08 supplies the owner-scoped engine and AP-07 implements durable installation,
configuration, update/rollback, machine revision reads and installation observations.
AP-07 dispatch is opt-in and requires an installation-capable negotiated Daemon.
Hosted start/stop and run/runtime observation consumers remain AP-09/AP-10 work.
AP-05 deployment was confirmed; hosted registry/Auth0 and AP-07 operator deployment
remain explicit external gates. See [installation design](apps-installation.md).
[Manifest v1](apps-contract.md) defines release validation; the
[active plan](milestones/apps-pivot.md) tracks implementation.
[Device protocol v1](apps-device-contract.md) defines the separate exchange.

OpenAPI: [hosted API](../packages/contracts/openapi/ringfour-api.v1.yaml) and
[local runtime](../packages/contracts/openapi/ringfour-runtime.v1.yaml). Operations carry `x-implementation-status: implemented` or `contract-only`
according to the active milestone.
[Resource definitions](../packages/contracts/schemas/apps/v1/api.schema.json),
[mutation inputs](../packages/contracts/schemas/apps/v1/requests.schema.json),
and [App errors](../packages/contracts/schemas/errors/v2/app-error.schema.json)
are separate from legacy schemas. Limits and semantic invariants below are
mandatory in addition to JSON Schema; fixture oracles are test support, not
shared application code.

## Identity and authority

| Record | Identity, ownership and relationship |
|---|---|
| App | Server UUID; unique `(publisher_organization_id, app_key)`. Publisher is derived from authenticated publishing workspace. |
| Revision | Server UUID; unique `(app_id, manifest.release)`. Manifest, canonical digest and publisher provenance are immutable. Visibility is fixed for this initial release contract. |
| Share | Server UUID, one immutable revision and destination workspace, grant actor/time, expiry and optional revocation time. A guessed UUID does not authorize access. |
| Import | Server UUID; unique `(organization_id, revision_id)`. Retains original publisher, App and revision. Source is server-derived `store` or `direct_share` with grant ID. |
| Installation | Server UUID; unique `(robot_id, app_id)` in v1. Several Apps can be installed on one robot. Robot workspace and App identity never change. |
| Configuration revision | Server UUID, append-only complete validated values bound to one installation and release schema, with actor/time. Never overwrite a prior version. |
| Run | Server UUID and shared execution-row UUID for AP-02; references installation and an immutable snapshot. No mission record is required. |
| Operation | Server UUID for one durable action, independent of the run UUID. Start and stop reference their exact run. Install/configure/update/rollback have `run_id: null`. |

Owner/developer in publisher A may publish and grant/revoke revision shares.
Authenticated members of a selected workspace may browse listed releases.
Owner/developer in destination B may import/install/configure/update/rollback
on B's robots. Operator/developer/owner in B may inspect/start/stop installed
Apps and respond to their interactions. Membership is rechecked per action.
A publisher listing or grant never authorizes access to consumer robots,
configuration, output, artifacts, interactions or history.

For direct imports, resolve the grant server-side, require the exact revision,
publisher, destination workspace and unexpired/unrevoked grant at import time,
and persist the source/actor atomically with the import. Missing-grant advice
(`share_required`) is returned only when the revision is already readable; a
guessed inaccessible private revision remains scoped 404. Store installs can
create this authorized import in the same transaction as the installation and
operation. An existing authorized import is reused. A later grant revocation
prevents new imports through that grant; it does not revoke already imported
revisions or rewrite provenance. Import deletion/revocation is not introduced
in this slice. Cross-workspace or inaccessible object lookups return scoped
404; insufficient action role in an accessible workspace returns 403.

Publishing verifies registry media type, concrete platform digest and immutable
manifest before a revision becomes available. Identical content at the same
App/release resolves to the existing revision; different content conflicts.
Listing, discovery and imports are metadata operations, not image-layer storage.
Registry pull credentials are machine-scoped and short-lived, never persisted
in these resources, App environment or durable command payloads.

## Operations, application and execution

All hosted mutations require `Idempotency-Key` (1–200 characters). Scope it to
principal, route, target and canonical request. Commit the key, resource IDs,
operation and response atomically. Same key/input replays the original IDs and
result **before** checking current mutable state; changed input returns
`idempotency_conflict`. Retain the minimal replay tombstone for the lifetime of
the referenced records, including removed-resource tombstones. No automatic
expiry is introduced for App mutations. The legacy optional 24-hour header
contract is unchanged. Natural App/release and workspace/revision uniqueness
also deduplicates publication and import under different retry keys.

Install/configure/update/rollback return 202 with `installation_id`,
`operation_id` and `run_id: null`. A start atomically claims the shared robot
execution slot across App and legacy sources, allocates the run/snapshot and
operation, and returns their stable IDs. A second active start returns
`execution_conflict`. A stop returns an operation for an existing run and never
creates a run. Preconditions and authorization are checked before any new run
or device dispatch. Start responses mean **accepted**, not running.

| Operation state | Phase and evidence |
|---|---|
| queued | Hosted durable intent; no device acknowledgement or observation. |
| accepted | Daemon durably recorded the command. `acknowledged_at` is present; no outcome is implied. |
| running | Observed progress: checking/staging/staged, starting or stopping, appropriate to kind. |
| succeeded | Device observed applied (install family), observed_running (start) or stopped (stop). |
| failed | Structured failure with a stable code and retry advice. Device observation may be absent for server-side expiry. |
| cancelled | Terminal cancellation before completion; does not erase a run or imply a running container ended. |

Progress may skip intermediate observations (for example, a fast start/exit),
but state and phase cannot move backward. Terminal results/timestamps are
immutable; identical replays are accepted. Start succeeds after evidence that
the container started, even if the run has already terminated before reporting.
Every operation has an absolute `deadline_at`. Expired undispatched commands
fail without new side effects. Retryable dependency failures may be retried
within that deadline with the same operation; a completed failure is never
reset. `details.retryable` advises a caller whether a **new** request/key is
appropriate after correcting the dependency; it does not authorize replay.

Available revision, candidate and installed binding are independent values.
The candidate progresses `staging → staged`, then is cleared when applied;
a failure remains visible as `failed`. A staged candidate does not replace
`installed`. Apply only while the robot is idle (including legacy execution
exclusion), atomically switching revision/config/image after a device report.
Failed configuration validation or preparation leaves the prior installed
binding usable. Install produces no run. Update/rollback produces no run.
Rollback chooses a previously installed, retained binding and configuration
from this installation and still performs current compatibility checks.

Configure, update and rollback use expected revision/configuration IDs to
prevent overwriting intervening edits (`stale_installation`). Configure validates
a full replacement object against the installed release. Update validates
against the candidate release. Start requires expected installed revision and
configuration IDs. Only one pending mutation per installation is allowed;
conflicting new keys return `stale_installation` until it resolves. Immutable
configurations are readable only through their installation.

Stop requires `{expected_run_id}` for the installation. Resolve it to a
persisted run and bind that ID into the operation; never dispatch a selector
like "whatever is active". If a different nonterminal run is active, return
`active_run_changed`; if the intended run has already ended, record a no-op
success for that run without acting on a later one. Same-key retries always
return the original operation. The device must independently enforce this
binding, enrollment/deployment ownership and deadline (remaining device slice).

Run states are `accepted → running → succeeded | failed | stopped | interrupted`;
fast outcomes can skip a delivered running observation. Preparation refusal can
end an accepted run as failed; never fabricate a start timestamp. Missing a
possibly started container yields `interrupted`, `container_lost`, unknown exit
code and no automatic relaunch. A new user start is a distinct run. Run snapshots
freeze App/revision/import/publisher, image digest reference, manifest digest,
configuration revision and values, actor and installation. Updating, unlisting
or uninstalling cannot rewrite that evidence. Preserve legacy run IDs without
inventing an image or converting archived source into an App.

## Timestamps and observations

All timestamps are UTC RFC3339 with seconds and optional 1–6 fractional digits;
invalid calendar dates and offsets other than `Z` are rejected. Compare instants
at microsecond precision, not lexicographically. Do not order different device
and server clocks as though synchronized.

`created_at`, `published_at`, `granted_at`, `imported_at` and `requested_at` are
server commit/intent times; corresponding `*_by` fields are local user UUIDs
resolved from Auth0. `acknowledged_at` is the server receipt of the durable
device acknowledgement. `observed_at` is the device's observation time and
`received_at` is the server receipt of that observation. `completed_at` is the
server's durable terminal outcome time. A server-side predispatch failure can
have no device observation; it must not invent one.

Run `started_at` and `ended_at` are actual observed engine lifecycle times, not
request/receipt times. Unknown end time stays null on interruption even though
`completed_at` is known. Binding `installed_at` records device application and
`observed_at` its confirmation. `last_observed_at` retains the last successful
installation observation across disconnects. It never proves current liveness.
Current/offline connectivity and fresh Agent inventory are separate robot
observations still to be specified in the device slice. Studio and CLI must
render last-known activity and its timestamp rather than relabeling an old
observation as current running.

## Runtime, output, Logbook, artifacts and HITL

`/v1/app-runtime/*` is a private Daemon namespace, separate from legacy
`/v1/runtime/*`. The App receives only its per-run credential and validated
configuration. Credential validation uses Daemon-owned durable run ownership,
expiry and revocation, with no Runner lookup. The context endpoint resolves
attribution from that credential. Payloads cannot select a run or organization.
Revoke write authority at terminal completion; already queued data can continue
syncing through Daemon machine authority. Never pass human, registry or engine
credentials to an App.

Runtime entry/interaction bodies are limited to 8,192 UTF-8 bytes; artifact
publication metadata to 4,096 bytes. Entry/request/artifact IDs are stable replay
keys scoped to the credential's run. Same ID/input replays; changed content
conflicts. Local acknowledgement requires durable state plus outbox; hosted
availability is independent. Runtime Logbook evidence must name artifacts owned
by the same run. Artifacts are regular files under a run-owned area, with no
symlink/traversal escape; Daemon snapshots immutable bytes before acknowledgement
and retains them until upload/evidence is durable. Limits: 16 artifacts/run,
16 MiB/artifact, existing content-type allowlist. No Runner snapshot API is used.

Output uses independent stdout/stderr byte offsets and base64 chunks of at most
16,384 decoded bytes, at most 65,536 bytes retained per stream in v1; truncation
is explicit. `next_offset = offset + decoded length`; offsets must address the
retained byte range. `sealed` means the local journal is final, not that all
bytes are hosted. Stop must finalize output before cleanup; pending upload must
not vanish with the container. App-scoped and global App Logbook filters return
the same run/event UUIDs, with separate robot events retained. The new v3 page
is additive; AP-10 must combine legacy and App history in the product read model
without breaking the existing legacy history endpoint or its parser.

Interaction v2 replaces legacy `execution_id` attribution with `run_id` in the
new namespace. A submitted response has no confirmation timestamp and leaves
the interaction pending. Only device acceptance permits `applied` and answered;
confirmed rejection remains distinguishable. No deadline is the default;
bounded wait expiration leaves pending. Persist original response actor,
submission and confirmation times. Cross-run/org reads and responses fail even
with known IDs. Legacy interaction semantics and wire fields remain unchanged.

## Errors, consumers and persistence gate

All new routes use the standard error envelope with a bounded App-specific
code/details vocabulary. `inventory_unavailable`, `missing_required`,
`no_matching_image`, `native_requirement_unavailable` and `pull_failed` are
separate outcomes. Missing optional capabilities are explanatory and do not
block. `daemon_upgrade_required` identifies a peer without negotiated support;
`ownership_conflict` and `container_lost` must never trigger blind recreation.
Do not expose registry tokens or full engine inspections in error messages.

Daemon inventory/negotiation and CLI/Studio summary readers are independently
tested against the same fixtures. AP-03 wires inventory into the Daemon and
API; CLI/Studio product surfaces remain later slices. Their implementations
must consume the same state, failure, ID and observation fields.
Negotiation/inventory schemas and cross-language reader fixtures are defined in the device contract.
AP-02 adds the Apps PostgreSQL persistence model and a shared execution-slot
constraint across App and legacy execution rows. The approved reset boundary
initializes a fresh application schema and restores only users, organizations,
memberships/roles and Auth0 subject mappings with their IDs. Historical
application rows, outputs, artifact bytes, HITL timestamps and pending work are
discarded under the explicit user-authorized reset; only identity JSON is
retained. Full application backup gates were waived for this reset. AP-02
preserves device SQLite enrollment/outbox data; AP-03 adds inventory reporting.
Installation/run writers and dispatch remain gated by their corresponding milestones.

Socket-test migration remains narrow: `tests/e2e/04-runner.sh` continues rejecting
Runner sockets. `tests/e2e/06-missions.sh` keeps its no-socket assertion for the
legacy fixture. AP-08/08V adds an explicit positive socket/ownership assertion
for only the authorized App Daemon and negative assertions for server, Runner
and App containers. Do not weaken the blanket legacy check ahead of that slice.


## AP-05 registry and catalog contract

New design: `POST /v1/device/app-registry-grants` refreshes ephemeral pull
credentials, bound to a current negotiated machine session, nonterminal and
unexpired install/update/rollback operation, installation candidate operation,
revision and exact image target. The candidate and its workspace import are
server-owned authority. Recheck them after the registry exchange before returning
credentials. This endpoint creates no operation or installation; AP-07 supplies
those writers. Machine requests do not use human mutation idempotency headers.

Only operator-allowlisted registry hosts and repository prefixes are contacted.
Private policies additionally require an explicit publisher workspace binding;
a consumer cannot bypass a private release grant by republishing its known digest
under another publisher. The grant endpoint derives that publisher from the
stored revision, independently of the consumer robot workspace.
HTTPS is mandatory outside Development. Development-only explicit HTTP permits
isolated registry acceptance. Redirects and implicit proxy discovery are disabled.
Registry manifest/config responses are bounded; verify SHA-256 over actual bytes,
matching response media type and concrete manifest/config OS/architecture.
Reject indexes and failed or redirected fetches before inserting catalog metadata.

Private registries must implement the Distribution Bearer token exchange with an
operator-configured exact token endpoint and service. The API asks only for
`repository:<repository>:pull`, never relays an arbitrary challenge URL and never
returns long-lived Basic credentials. Reject token lifetimes beyond five minutes.
Anonymous pulls require explicit operator policy. Repository-scoped tokens can
permit other digests in that repository; device execution must still enforce the
authorized image digest. Tokens exist only in memory/response and responses use
`Cache-Control: no-store`. Errors and logs exclude credentials.

Publisher role is checked before registry access. Identical publication and
idempotency replays do not need the registry to remain online. Different manifest
content or visibility under an existing release conflicts. App display metadata is initialized on first publication and updated only by
a listed publication. Unlisted revisions keep their display changes within the
authorized revision manifest. Immutable revisions keep original manifest content. Import/revocation serialize on the share row: existing imports
retain their original source/actor after expiry or revocation; new imports require
a valid grant. Reads of a valid directly shared revision also permit its App
metadata. Pagination uses creation/publication time and UUID, defaults to 50,
limits to 100 and rechecks authority on every page. Search is literal,
case-insensitive App name/key/description matching, bounded to 200 characters.

Hosted App JSON bodies are bounded to 65,536 bytes, including publication wrappers.
No AP-05 database migration is required; AP-02 supplied the catalog, import/share
and permanent App idempotency tables. Secrets and ephemeral grants are not stored.
