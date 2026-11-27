# AP17 contract decisions

Status: AP17.01 contract baseline. This addendum defines new App v2 and live
settings interfaces. It does not alter the immutable v1 manifest, its digest,
or existing run/configuration records. Implementation follows this contract;
the project-root [AP17 specification](../../AP17.md) remains the product
acceptance source.

## Manifest v2

Manifest and authoring documents use `schema_version: 2` and their own strict
schemas under `packages/contracts/schemas/apps/v2/`. They retain all v1 fields.
The v2 `configuration` object may add `environment`; the top-level manifest may
add `runtime_settings`. Both are optional and unknown properties remain errors.
An App with no `runtime_settings` does not support live settings. V1 records are
read and executed using their original canonical bytes and digest.

`configuration.environment` is an object mapping environment variable names to
keys declared in `configuration.schema.properties`. It has at most 32 entries.
Names match `[A-Z_][A-Z0-9_]*`; values name existing configuration keys.
Reject duplicate names (including duplicate JSON members), undeclared keys,
NUL values and mappings that violate the reserved-name rules below. An absent
optional configuration value produces no environment override. Strings are
passed unchanged, integers as canonical base-10, fractional numbers as
ECMAScript-compatible shortest round-trip IEEE-754 binary64 decimals, and
booleans as lowercase `true`/`false`. The rendered mapping is limited to
16,384 UTF-8 bytes, and a
mapped string containing NUL is refused. Never interpolate through a shell.
The complete validated configuration JSON file remains available as before.

Reserved names are `RINGFOUR_` prefix; exact name `PATH`; prefixes `LD_`,
`DYLD_`, and `PYTHON`; and exact names `IFS`, `ENV`, `BASH_ENV`, `BASHOPTS`,
`SHELLOPTS`, `NODE_OPTIONS`, `NODE_PATH`, `JAVA_TOOL_OPTIONS`, `_JAVA_OPTIONS`,
`RUBYOPT`, `RUBYLIB`, `PERL5OPT`, and `PERL5LIB`. These rules prevent Ringfour
runtime collision and common process/interpreter loader overrides while
allowing ordinary App-specific variables.

`runtime_settings` is exactly `{ "schema": <configuration schema>, "defaults":
<complete values> }`. It uses the existing closed, flat
string/integer/number/boolean subset and all current key, byte, scalar, enum
and bounds limits. Defaults are
validated as a complete object. Secrets are unsupported and values are retained
in immutable revisions/history. This is not a secret store.

## Live-settings identities and transitions

An installation has one selected settings revision for future runs (nullable
only before the first saved value). Each run snapshots that selected revision at
start. The run's last confirmed applied revision begins unknown. Each settings
edit creates an immutable revision. Active edits additionally create one
run-scoped application request with a stable `application_id`, full values,
expected previous applied revision, expected installation selection, target
run, and actor/time provenance.

Application outcome states are `pending`, `applied`, `rejected`, and
`ended_before_confirmation`. Delivery state is separately `queued`, `delivered`,
`locally_received`, or `reported`. `pending` is unresolved; no second
unresolved application may exist for a run. `applied` and `rejected` are
terminal. Ending a run with an unresolved request marks it
`ended_before_confirmation`; it is never delivered to another run. A repeated
result for the same application ID and identical content succeeds. A different
result for that ID conflicts.

| Event | Selected for next start | Applied to this run | Application record |
|---|---|---|---|
| Valid edit while idle, expected selection matches | New revision | Not applicable | None; return `saved_for_next_start` |
| Valid edit while active, run/selection/applied preconditions match | Unchanged | Unchanged | New `pending` application |
| App confirms application | Promote candidate only if the captured selection still matches | Candidate revision | `applied` |
| App rejects application | Unchanged | Prior confirmed value | `rejected` with bounded safe reason |
| Run ends while pending | Unchanged | Last known value | `ended_before_confirmation` |
| Late confirmation after run end | Compare-and-set; never overwrite a newer selection | Old run's historical applied value | `applied` for old run |

Initial settings are delivered to an opted-in App before it performs behavior
that depends on those settings. The run remains separately `running`; Studio
shows `App running · settings pending` until confirmation. Rejection is
prominent and the example exits without using unconfirmed settings. Idle save
means “saved for next start”, not App-validated or applied.

## Hosted API

All routes are workspace-authorized and use existing App installation roles.
Reads follow configuration visibility. Mutations require owner/developer
authority. Cross-workspace resources retain scoped not-found behavior.

### `GET /v1/app-installations/{installationId}/settings`

Returns the selected schema/release, selected next-start revision and values,
current run ID when active, last confirmed applied revision, pending
application, and capability state (`supported`, `unsupported`, or
`upgrade_required`). Unsupported Apps do not receive an editable runtime form.

### `POST /v1/app-installations/{installationId}/settings`

Requires `Idempotency-Key` and a complete `values` object. The request also
contains nullable `expected_selected_revision_id`, nullable
`expected_applied_revision_id`, and nullable `expected_run_id`. The server
validates against the pinned manifest and derives idle/active state from durable
records; request fields never select another workspace, installation or run.
Each expected value must match current state or the server returns a conflict.

An idle response is `{ "state": "saved_for_next_start",
"settings_revision_id": ..., "selected_revision_id": ... }`. An active response
is `{ "state": "pending", "application_id": ..., "settings_revision_id":
..., "run_id": ... }`. The application ID is the stable operation identity.
The server accepts at most one unresolved application per run.

### `GET /v1/app-installations/{installationId}/settings/revisions/{revisionId}`

Returns the immutable revision and provenance after the same workspace check.

### Release update and rollback

Update and rollback accept an optional complete `runtime_settings` object. If
omitted, update reuses current values only after validating them against the
target release; rollback first restores the most recently retained values for
that release, also validating them. Invalid reuse returns
`stale_settings_selection` and requires corrected values. Explicit
`runtime_settings: null` selects the target release defaults and clears the
previous selection. A candidate settings revision is bound to the release and
becomes selected only after the Daemon reports the installation binding
applied. Failed staging leaves the prior selection intact. Releases without
live settings require explicit null to clear an existing selection.

Settings writes also add stable Logbook events: `app.settings.selected` for an
idle next-start selection and `app.settings.application_requested` for an
initial or live exact-run application. The event payload carries settings and
application revision identities, never the settings values. Applied and
rejected outcome events are written from Daemon result delivery.
If the run ends while an application is unresolved, it becomes
`ended_before_confirmation` and remains unable to affect a later run.

Conflict/error codes are `settings_unsupported`, `daemon_upgrade_required`,
`stale_settings_selection`, `active_run_changed`, `settings_pending`,
`invalid_input`, and `settings_rejected`. Existing error envelopes and HTTP
status conventions apply; errors never echo setting values.

## Runtime delivery

Deployment remains opt-in. The Platform API environment variable
`APPS_LIVE_SETTINGS_ENABLED=true` enables the API side after its additive
migrations are applied at startup. The Daemon advertises live settings only
when Apps are enabled and its runtime-settings implementation is present. The
operator rebuilds/reloads the Platform API and the `ringfour-virtual` Apps
topology separately; preserve existing PostgreSQL and Daemon data volumes.

The App uses the existing run-scoped credential and local Daemon HTTP boundary.
The credential determines the run; no body or query argument can select one.
Add `GET /v1/runtime/settings/pending` to return at most one pending
application for that run, or an empty pending result. Add
`POST /v1/runtime/settings/{application_id}/result` with `outcome` equal to
`applied` or `rejected`, and an optional bounded, single-line safe reason for
rejection. The Daemon persists receipt/result locally before acknowledging the
App, then retries its authenticated machine outbox to the API. Active edits use
the existing device command channel: the Platform persists an AppOperation of
kind `settings.apply` and dispatches `app.settings.apply` to the negotiated
Daemon. The command's `operation_id` is the stable `application_id`. The Daemon
commits its local deduplication/settings-pending record before acknowledging
the WebSocket command; that acknowledgement means delivered to the Daemon, not
applied by the App. App results flow through the existing ordered
`app-observations` outbox/receipt route as a settings operation observation.
Bind each command/result to workspace-derived installation ownership, robot,
deployment namespace, exact run and revision. Reuse current operation replay
and session fencing. Identical results are idempotent; conflicting results
fail.

At run start, the API freezes the selected settings revision and generates its
initial application ID in the run snapshot. The Daemon seeds this initial
application into its local pending queue when accepting the start snapshot.
This does not create another execution or change the execution slot. The App
must poll and confirm initial settings before settings-dependent behavior.

Polling is bounded to 1–30 seconds; request and response bodies are at most
16,384 UTF-8 bytes. The App helper retries with the same application ID and
retains an in-process completed result so an acknowledgement retry does not
invoke the handler twice. This is at-least-once delivery, not exactly-once
domain effects. A timeout means unresolved, not rejected or reverted.

Device App negotiation adds optional boolean offers `supports_manifest_v2` and
`supports_live_settings`; omitted means false. The new Daemon advertises both
only when the corresponding implementation is enabled. The current App
protocol envelope remains version 1. The API stores each offer on the current
session and refuses fresh v2/live-settings dispatch and replay unless the
current session negotiated the needed feature. Old Daemons continue to use v1
Apps and workflows.

## Cross-run Files query

`GET /v1/apps/{appId}/artifacts` is implemented with required `organization_id` and optional
`installation_id`, `robot_id`, `run_id`, `path_prefix`, `cursor`, and `limit`.
Workspace membership is checked before filtering. All supplied resource filters must resolve inside that same
workspace and to the selected App; otherwise return scoped not-found. Ordering
is newest server publication receipt first, then artifact ID. Default page size
is 50; maximum is 100. The opaque cursor binds the order key and normalized
filter tuple (workspace, App, installation, robot, run, prefix); changing
filters while continuing a cursor is `invalid_input`.

Each result includes the existing artifact ID, run, installation, robot,
relative path, byte size, SHA-256, content type and server `published_at`. The
receipt timestamp is stored prospectively; older rows return null and clients
label their date unavailable. Repeated relative paths in different runs remain
separate items. Folder rows are client-side groupings of the complete paginated
file results; `path_prefix` enables incremental folder browsing. Content uses
the existing authorized download route and digest verification. Only confirmed
hosted publications appear.

## New Ringfour interfaces

Manifest v2 fields, feature negotiation, settings revision/application
resources, runtime polling/result operations, and the cross-run artifact query
are new Ringfour interfaces. Existing v1 manifest, API routes, run credential,
artifact upload/download, Logbook identities and execution slot are reused and
remain compatible.

### Device command ordering

An authenticated run-scoped settings command may arrive before its matching
start command. The daemon persists and acknowledges that intent without
reconnecting or applying it to another run. Runtime polling/results still
require the exact active run credential and verified running container.
Duplicate application IDs must retain the same run, installation, revision,
values and initial flag.
