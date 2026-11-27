# App device protocol v1

A session negotiates inventory, installation, run and uninstall support. See
[installation gates/recovery](apps-installation.md) and [engine policy](apps-engine.md).

## Separate support negotiation

The negotiation request has optional `supports_app_installations` and
`supports_app_uninstall` fields. Omission means false;
uninstall support requires installation support. Inventory-only peers remain
compatible, but cannot receive installation commands or submit installation
observations. App mode verifies the engine before offering these capabilities;
the response and legacy hello/welcome stay unchanged.

Keep `/v1/device/connect`, its envelope version 1, hello, welcome, ack,
heartbeat and legacy command/status shapes unchanged. Do not add App fields to
hello or welcome; both disallow unknown fields. After receiving the normal
welcome, an App-capable Daemon can call
`POST /v1/device/app-protocol/negotiate` with its current `session_id`, stable
`deployment_namespace` and supported App payload versions (up to 8 distinct
positive integers). The server selects 1 only if offered, returning the same
session/namespace, selected version and server time. No common version yields
409 `daemon_upgrade_required`. Unknown endpoints on an older server disable
App support without downgrading App commands to legacy execution.

Authenticate with both enrolled machine credential headers, not Auth0.
Require the session to be the active, accepted v1 connection for that same
robot. Bind the selected App version and namespace to that session. Identical
negotiation retries return that selection; conflicting namespace/version offers
within the same session fail. Reconnect requires new negotiation. A cached
selection, Daemon version string, profile name or online heartbeat is not proof
that the current connection supports Apps. A legacy Daemon never negotiates and
receives no App commands. Report `support: not_negotiated` or `upgrade_required`
explicitly; do not collapse this into missing capability or offline state.

Filter replay as well as fresh dispatch: hold all `app.*` commands until current
session negotiation succeeds. Replayed operation content, ID, payload version
and deadline remain unchanged across sessions.

## Ownership and command payloads

Provision one durable deployment namespace UUID alongside the enrolled robot
identity in Daemon data. Migration 0015 backfills one for existing enrolled
devices, and new enrollment creates it in the same SQLite transaction as the
identity binding. Replacement retaining the Daemon database retains this
namespace; a different enrollment cannot adopt it silently. Enforce a single
writer/fence for that enrollment/namespace.
A session replacement invalidates the old session's authority. The Daemon uses a lifetime database/namespace writer fence and serialized engine
mutation journal; installation dispatch requires this fence.

Canonical workload labels are:

| Label | Durable value |
|---|---|
| `eu.ringfour.robot-id` | Enrolled robot UUID |
| `eu.ringfour.deployment-namespace` | Provisioned namespace UUID |
| `eu.ringfour.installation-id` | Installation UUID |
| `eu.ringfour.run-id` | Exact run UUID |

Filter engine lists by robot and namespace, then verify all four labels,
recorded engine container ID, exact image and owner-controlled network before
any mutation. A profile name or run label alone is insufficient. Wrong-owner,
ambiguous matches and conflicting deterministic names fail without mutation.
Engine administrators remain trusted; labels do not isolate hostile engine
tenants. Never globally prune shared digests or resources.

App commands reuse the v1 durable `command` envelope, with `payload_version: 1`
and one of `app.install`, `app.configure`, `app.update`, `app.rollback`,
`app.uninstall`, `app.start`, `app.stop`. Uninstall carries the exact installation
and expected installed revision/configuration IDs; it has no release descriptor
or registry pull. It is dispatched only to a session advertising uninstall support.
The negotiated payload schema additionally validates
kind-specific bodies. The full manifest is fetched by credential/session and
operation from `/v1/device/app-revisions/{revision_id}`; the server authorizes
that exact revision through the robot's nonterminal operation. Known IDs alone
are insufficient. The device verifies the canonical manifest digest and selected
concrete image/platform. No expiring URLs, registry credentials or host deployment
settings are stored in a command.

Install/configure/update/rollback carry installation, stable namespace,
expected installed revision/configuration and a release descriptor containing
App/revision/manifest digest, selected target and validated configuration.
Expected revision/config IDs are both null for a fresh install or both concrete.
Compare them against durable device state before applying a mutation. Start
carries installation/run/namespace and the immutable hosted snapshot. Stop names
`expected_run_id` and never resolves an active selector at execution time.
Bind all IDs back to authenticated robot intent. Check current inventory before
pull and again before start. No matching image, missing hard capabilities,
unavailable inventory, native prerequisites and pull failure are distinct.

On replay find the same durable run/container or terminal result. Never relaunch
a possibly started run whose container is absent; report interruption with
unknown exit/end time. An accepted start that is safely known never to have
started may proceed through its persisted intent. The Daemon journals these
boundaries before engine calls and preserve output before cleanup.

## Observed Agent inventory and target

The Daemon uses a minimal JSON-RPC 2.0 `hello` reader matching the
SDK's existing protocol 1 over `unix://` or `ws://`/`wss://`. The call has a
bounded timeout and 1 MiB welcome limit (the Agent may include a description).
It projects only profile/capability/instance metadata and sends no robot action.
`RINGFOUR_AGENT_URI` enables reporting. With Apps enabled, the Daemon obtains engine facts from its owner-scoped adapter. In inventory-only mode, configure `RINGFOUR_ENGINE_OS` and `RINGFOUR_ENGINE_ARCHITECTURE` for the deployment engine. Missing values make the target unavailable.
`RINGFOUR_ENGINE_NATIVE_FACTS` is a comma-separated, operator-verified list.
The Daemon refreshes inventory every 15 seconds by default and retains only
observations as explanatory cache; each later install/start must call the fresh
reader again. PostgreSQL persists negotiated session/namespace, latest inventory
projection and report deduplication. SQLite migration 0016 persists the last
successful Agent observation alongside the existing durable report outbox.

`PUT /v1/device/app-inventory` uses the machine credentials and the
`x-ringfour-device-session` header for a currently negotiated session. The body
contains App protocol version, namespace, report ID/sequence, engine platform
and an inventory union:

- `available`: a bounded attempt returned a current `agent_welcome` observation,
  with Agent protocol 1, profile name/model/version, advertised capabilities,
  named instances and device observation timestamp.
- `unavailable`: attempt time/reason and nullable `last_successful`. It has no
  `current` value. Cached data remains explanatory and cannot satisfy a fresh
  install/update/start check.

No sensor sample is part of inventory. Advertising `imu` proves neither a fresh
reading nor method permission. Preserve unknown, well-formed observed capability
IDs and instance categories as information; manifest v1 accepts only its
explicit predicate vocabulary. Capability and instance lists are bounded and
unique. Nonempty `instances.arms` must be a subset of named groups with the
`groups` capability; generic groups alone never establishes an arm. An arm
predicate requires both `groups` and a declared arm instance. A missing optional
predicate explains a limitation without refusing a compatible App.

The pinned Base101 source provides exactly mobility, cameras, lidar, imu, slam,
teleop, watches, places, audit, status, and `cameras: [base]`. This fixture is
traceable to SDK `models/protocol.py::Welcome/ProfileInfo` and Agent
`models/profile.py::RobotProfile.instances` at the baseline recorded in the
backlog. It is not evidence of a running sensor or live handshake here. Both
independent virtual robots can use profile name base101; enrollment and
namespace establish ownership, not profile name.

Report engine target OS/architecture, not the Daemon image's emulated platform.
Normalize x86_64 to amd64 and aarch64 to arm64. Other well-formed targets remain
reportable and yield no matching image against current manifest targets. Native
facts are verified deployment/host prerequisites, never publisher assertions or
permission to install a driver or mount a device. Unknown/unverified hard
prerequisites fail closed. Agent still owns ROS and safety.

## Atomic observations and replay

`PUT /v1/device/app-observations` reports bounded operation, installation and run
observations, without supplying human actors, organization identity or server
receipt/completion timestamps. Derive those from the authenticated robot and
persisted intent. Validate referenced IDs and namespace against the operation,
installation and immutable run before changing state. The batch is atomic.

A v1 ack still means the command was durably recorded, never that it installed
or ran. App operation progress/completion comes through this negotiated batch;
reject App terminal updates via legacy `operation_status` alone. An applied
operation requires the corresponding installed binding in this or an already
persisted observation. A successful start requires evidence of the exact run's
start, including fast exits; a stop requires that exact run's stopped/terminal
state. Staging keeps the prior installed binding. Applying during any active
App or legacy execution is forbidden. Public state/phase/error mapping is
specified in [API/runtime contract](apps-api-contract.md).

Bodies are at most 65,536 UTF-8 bytes; each observations array has at most 32
entries. Reject the whole batch if invalid or too large. Reject duplicate JSON
members, non-JSON numbers, malformed Unicode, unknown versions/fields and
conflicting duplicate object IDs. Safe integer constraints permit equivalent
integral JSON number forms (for example 1.0), without coercing strings/booleans.

Each route is an independent durable stream keyed by enrolled robot, namespace
and route. Persist a strictly increasing safe-integer sequence starting at 1,
report UUID, canonical body digest and original acknowledgement. Deliver reports
serially per stream; do not send the next batch until the previous is acknowledged.
Same report ID/content returns the original receipt without changing timestamps
or reapplying state. Changed content/route/sequence for a known ID returns
`conflicting_report`. An unseen older sequence or a gap returns
`stale_observation`; resynchronize the durable outbox before advancing. Record
report deduplication and projection/outbox changes atomically. Never discard
unacknowledged terminal history merely because a newer observation exists.

The current session is an ephemeral request header, deliberately outside the
durable body/digest. A replacement with preserved enrollment/namespace can
resend the identical batch under its new negotiated session. Old-session
requests return `session_replaced`, including duplicate reports. Namespace
changes or conflicts require explicit operator enrollment recovery, not adoption
of engine resources. No registry/human tokens belong in reports or errors.

## Hosted status and compatibility preview

`GET /v1/robots/{robot_id}/app-status` is a workspace-scoped Auth0 read. It
reports current connectivity/support separately from platform/inventory and
last received report. `report_sequence: 0` has no observation. A positive
sequence requires timestamp, namespace, platform and inventory provenance.
Supported means current-session negotiation is recorded. Offline projection
retains original observation/receipt times and never relabels cached activity
as a current reply.

`GET /v1/robots/{robot_id}/app-compatibility?revision_id=...` authorizes the
revision independently through listing, publisher membership, destination share
or existing import. Preview has no pull/install side effect or dispatch
permission. Offline robots return `inventory_unavailable`. For online robots,
deterministic refusal priority is: unsupported/unnegotiated Daemon;
unavailable or stale inventory; no matching engine target; missing required
predicates; missing native prerequisites; compatible. Preserve optional omissions
as explanatory arrays. Do not report absence from unavailable inventory as a
proven missing capability. Use the existing 45-second presence threshold and
server receipt time for preview freshness; actual device operations must obtain
a current bounded Agent reply regardless of the preview.

`checked_at` is server preview time; `inventory_observed_at` identifies the
underlying observation or is null. Agent-unavailable and offline views can show
`last_successful` while returning `inventory_unavailable`. A compatible response
has a selected target, known observation time and empty hard-blocker lists.
CLI/Studio use the same status/code/observation fields; the typed summary readers
share fixtures with the API contract tests. No first-party UI session or Gradio
replacement is added.

App commands
are acknowledged through ack and their durable outcomes use App observations.
