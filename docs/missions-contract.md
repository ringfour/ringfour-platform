# Mission v1 — new Ringfour design

Status: **locally verified**. These interfaces are new Ringfour design and do not describe
existing SDK or Agent APIs. Physical Balena verification remains pending.

## Local project and immutable revisions

A mission directory contains `mission.toml`, an entrypoint Python file and a
UTF-8, hash-pinned requirements lock. `mission.toml` contains `schema_version =
1`, `name`, relative `entrypoint`, `python_requirement`, relative `lock_file`
and optional `robot_profile_ref` (an external Agent-owned reference). The CLI
computes the source digest; the **published** v1 manifest contains the same
fields and a required `source_digest`. There is no SDK call or robot-profile
authoring in this contract.

Paths are relative, normalized POSIX paths using ASCII letters, digits, `_`,
`-`, `.`, and `/`. Reject absolute paths, empty/`.`/`..` components, symlinks
and special files. The entrypoint ends in `.py`. The CLI packages regular files
from the mission directory, excluding `mission.toml`, `ringfour.context.json`, hidden entries and the
specified lock source; it places the lock at `requirements.lock` in the bundle.
An existing source file named `requirements.lock` is only accepted when it is
the selected lock file. The generated manifest's `lock_file` is therefore
always `requirements.lock`. The same archive, file-count, per-file, total and
canonical SHA-256 rules as milestone 05 apply. A digest in the published
manifest covers the packaged source files and lock, not `mission.toml`, so it
does not depend on itself. Published manifests and revision associations are
immutable.

`POST /v1/missions` creates an organization-owned mission. A developer publishes
an immutable ready milestone 05 script revision with
`POST /v1/missions/{id}/revisions`, supplying the manifest and expected current
version. The Platform verifies the linked revision belongs to the same
organization and matches its digest, entrypoint, Python requirement and lock.
Version numbers increase from 1; a stale expected version returns 409. Mutation
requests require principal/route/target-scoped idempotency keys. Matching retries
return the original result; conflicting payloads return 409. The Platform does
not claim a revision is available until its 05 source upload was finalized.

## Deployment and lifecycle

Each robot has at most one selected mission deployment. Deploying a candidate
requires developer access, an online daemon and an immutable mission revision in
the robot's organization. Replacing a selected revision while any execution is
active is rejected with `execution_conflict`; no process is preempted. At most
one candidate preparation per robot is outstanding. Platform and daemon persist
the candidate and stable operation ID before side effects. The daemon downloads
and verifies the candidate, then asks the private Runner to prepare its locked
environment. Selection changes atomically **only after** successful preparation;
failed or interrupted preparation leaves the previous selected revision intact.
The Runner's preparation and cache GC must not delete the selected or active
revision. Replay after a daemon or API restart uses the same candidate and never
switches to different bytes.

Desired state is `stopped` or `running`; observed state reflects preparation,
ready, execution and terminal results. Start creates a stable execution ID for
the selected revision and records the human actor. Stop changes desired state to
stopped and requests process-group termination. Restart is an explicit durable
stop-then-start action with a new execution ID; it never replays an uncertain
execution. A `running` desired state alone never causes automatic restart after
failure. There is no automatic restart policy in this milestone. Script and
mission execution share Runner's one-active-execution invariant and the hosted
per-robot active-execution constraint.

The Platform persists the selected/candidate revision, desired state, observed
state, operation IDs and any restart intent in PostgreSQL. The daemon persists
the candidate/activation operation in its existing durable operation records;
Runner owns the prepared environment. Actor and execution history reuse the 05
execution records. Machine credentials are scoped to their robot; human reads
and writes are scoped to the robot's organization. All externally visible state
transitions, errors, limits, and exact payloads must be frozen in OpenAPI and
JSON Schema before implementing them.

The public lifecycle uses:

- `POST /v1/missions` with `{organization_id, name}`; `GET
  /v1/organizations/{organization_id}/missions` lists organization missions.
- `POST /v1/missions/{id}/revisions` with `{script_revision_id,
  expected_version, manifest}`; `GET /v1/missions/{id}/revisions` lists immutable
  revisions in version order. A stale `expected_version` is 409
  `version_conflict`.
- `POST /v1/robots/{id}/deployments` with `{mission_revision_id}` returns 202
  `{deployment_id, operation_id}`. It starts preparation, not execution.
- `GET /v1/deployments/{id}` returns selected and candidate revision IDs,
  desired/observed state, the latest operation and execution IDs, and any last
  error. `POST /v1/deployments/{id}/start`, `/stop` and `/restart` return 202
  `{deployment_id, operation_id, execution_id}`; execution ID is null for a
  stop. A restart reserves a new execution ID before its durable stop-then-start
  workflow begins. Status is the authority for whether either action completed.

All mutations require `Idempotency-Key` (1–200 characters), retained for at
least 24 hours. Mission creation and revision keys remain for the lifetime of
their records. `start`, `restart`, publish and deploy require developer; stop and reads
require operator. Inaccessible resources are 404; insufficient role is 403;
offline submission is 503 `robot_unavailable`; conflicting active work is 409
`execution_conflict`; pending candidate is 409 `deployment_in_progress`.
Stop can supersede a pending start or restart and may be accepted while offline: it records desired stopped and delivers the
stop on reconnection. The robot is not assumed stopped until observed state says
so. Restart requires an online robot on acceptance; once accepted its intent is
durable across outages. An expired unacknowledged prepare operation fails the
candidate, leaving the selected revision unchanged. Accepted ambiguous
preparation must reconcile by the same ID before any activation.

PostgreSQL `missions` contains organization, creator, immutable name and a
principal-scoped creation idempotency key; name is unique within an
organization. `mission_revisions` contains mission, monotonic version,
finalized 05 revision, manifest, creator and idempotency key; version and keys
are unique within their respective scopes. `deployments` has one row per robot,
selected and pending revision IDs, pending operation ID, desired/observed state,
latest execution/operation IDs, last error and timestamps. `deployment_actions`
persists lifecycle action idempotency, actor, reserved execution ID and restart
workflow state. Database transactions serialize conflicting operations per
robot; a candidate cannot silently overtake another candidate. The daemon's
existing durable operation log tracks preparation and lifecycle delivery.

Private `POST /v1/revisions/prepare` accepts a staged bundle reference, digest,
entrypoint, Python constraint, lock digest and the selected digest to retain.
It checks the bytes and prepares a separate candidate uv environment before
reporting success; it never launches user code. A bounded cache collector may
remove old environments only while preserving the selected digest, the
candidate and any active execution's digest. This endpoint requires the same
private Runner bearer secret as 05. The machine retrieves a candidate with
`GET /v1/device/deployments/{deployment_id}/candidate?operation_id=...` using
its robot credential. It can only retrieve the candidate belonging to that
robot and pending operation; no human credential reaches the daemon.
Runner keeps prepared environments under digest-specific, generation-specific
paths and atomically publishes a small pointer only after `uv` preparation
succeeds. This avoids relocating Python virtual environments and leaves the
previous prepared environment untouched on failure. The managed prepared
cache retains at most 20 revision digests, excluding in-progress temporary
generations; collection preserves selected and active digests. A prepare request
conflicts with active execution and with another prepare (`preparation_in_progress`);
the daemon retries that specific error for ambiguous same-ID preparation. Script starts also
conflict while preparation is in progress. Existing 05 environments remain
usable and are governed by the 05 retention limit.

## CLI direction

The supported lifecycle commands are `mission publish`, `deploy`, and `mission
start`, `stop`, `restart`, `status`. In addition, the requested convenience
form `ringfour-cli missions/hello push` will publish and deploy that directory;
it must not silently start or preempt an existing execution. It accepts
`--robot ID`; when omitted, `RINGFOUR_ROBOT_ID` is used or, if the selected
organization has exactly one online enrolled robot, that robot is selected.
Multiple possible targets require an explicit ID. `mission init` may create an
example manifest and files only when explicitly invoked and must not overwrite
existing files.

## Local daemon status page

The existing read-only LAN page on port 8083 refreshes every five seconds. For
an enrolled daemon it shows a bounded list of locally recorded mission
preparation and execution commands, their entrypoint filenames, the latest
Runner execution state, and up to the last 8 KiB each of stdout and stderr from
that execution. A successful preparation is reported as prepared, not as proof
that the Platform has committed selection; a mission start command includes its immutable mission revision ID. The page
can report a mission as active when Runner reports its execution running. The Platform deployment status remains authoritative for activation.
The page makes authenticated, read-only calls to its configured private Runner;
when it is unavailable the page reports that condition and continues to show
locally persisted activity. This page introduces no new control API or persistent
data store. It is unauthenticated: anyone able to reach its configured LAN
listener can read displayed program output, which may contain sensitive data.

The Runner image carries the Ringfour SDK `0.1.0.dev0` wheel at
`/opt/ringfour/sdk/ringfour_sdk-0.1.0.dev0-py3-none-any.whl`
(SHA-256 `f6f1519e5fb72c6cd05367fc2fff1afa824e287a78a72a33636609dafaf8e459`).
Missions pin the wheel and dependencies in a hash-checked lock, and uv installs
them into a separate mission environment. The previous RoboCore wheel remains
available for existing revisions. The Agent and profile remain independently
managed. The supplied missions use `RINGFOUR_AGENT_URL`, falling back to
`ROBOCORE_AGENT_URL` for existing robot configurations.

## Local CLI context — new Ringfour design

A successful folder-first mission push stores local convenience context in
`ringfour.context.json` under that mission directory. It is not a source of
truth or a credential store and is excluded from mission bundles. Version 1
contains `version` and `targets`, with each target recording API URL,
organization ID, mission name and ID, robot ID and name, deployment ID and
published revision ID. Targets are keyed by API, organization and robot; push
updates only the matching target. Save atomically after deployment acceptance;
a failed local write is reported without claiming the accepted push failed.
Users can ignore `ringfour.context.json` in version control and can delete it safely.

Within a mission directory (or its subdirectories), lifecycle commands may
resolve an omitted deployment ID from the context for the selected API and
organization. Multiple robots require an explicit `--robot`; explicit IDs keep
their existing behavior. Before context-based lifecycle actions, fetch the
current deployment and ensure the selected revision is the one last pushed by
this directory; reject stale or pending context rather than operating a different
mission. Context-based logs retrieve the deployment's current last execution
from the Platform and verify its revision attribution. Explicit IDs override
context. If no matching entry exists, the CLI can recover an unambiguous matching
mission and deployment from the selected organization; ambiguous matches require
`--robot`. Malformed context fails clearly; secrets and bearer tokens are never stored. These are CLI-local behavior
changes; the Platform API and authorization remain unchanged.
