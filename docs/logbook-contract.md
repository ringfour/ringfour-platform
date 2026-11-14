# Logbook — meaningful mission and robot history

Status: **mission-authored entries, selected Agent motion records and Console history locally integrated; motion coverage and CLI remain incomplete**. These are new Ringfour interfaces, separate from the
Agent API. Milestone 07 remains incomplete; its artifact, credential and
retention work and outstanding acceptance gates are not satisfied by this work.

Logbook records meaning chosen by the mission author, plus a limited set of
lifecycle and safety-relevant events. It is not an automatic transcript of every
device operation, SDK call, sensor reading or velocity update. Existing
`robot_audit_events`, `script_execution_events` and Runner execution events are
separate records; they are not automatically a complete Logbook or a durable
per-device replicated stream. Production execution history, actor presentation,
query and output/artifact links still need their own contracts and implementation.
Development source and output must remain on the LAN as specified by milestone 07.

## Standalone SDK and Agent

The SDK and Agent must remain independently usable without the Platform,
daemon, Runner, Logbook service or Logbook configuration. Existing SDK and
Agent operations must not connect to Logbook or fail because it is unavailable.
Only a mission explicitly calling `logbook.info` requires the runtime; that
call raises a visible error when it is unavailable. Agent motion and stop paths
must continue to work with Logbook disabled or unreachable. Any future durable
Agent handoff must be optional and preserve those properties; enabling it must
not turn a Platform outage into a robot-control dependency.

## What belongs in Logbook

- **Author entries:** mission code explicitly records a message and structured
  fields to explain decisions or observations. The target Python interface is
  `from ringfour_runtime import logbook; logbook.info("Inspection skipped", station="B", reason="Operator reported maintenance")`.
  The separately installed `ringfour-platform-runtime` client and execution-scoped
  runtime have been tested locally and are included in the existing Runner image.
- **Mission lifecycle:** revision selected and mission started, stopped or failed.
  These are meaningful lifecycle changes, not every internal operation state.
- **Human interventions:** requested, answered, timed out, and control handed
  back. Milestone 09 owns the interaction workflow and its state contract.
- **Agent motion outcomes:** accepted or refused, completed or cancelled,
  verified stops and guard trips, preserving operation/session and responsible
  owner where reliably available. A continuous drive session is summarized as a
  lifecycle and outcome; individual velocity updates belong to optional
  diagnostic telemetry, not Logbook.

The mission-authored path and selected Agent motion records are implemented and
locally tested. Automatic mission lifecycle and human-intervention production
remain incomplete; other Agent motion paths, including vendor actions, are not
yet covered.
Automatic entries need stable IDs, durable source records and replay; absence of
an event is not proof that a motion or intervention did not happen.

## Mission-authored entries

The private `POST /v1/runtime/logbook/entries` accepts the bounded
`runtime-entry-request` schema: an entry UUID, `info` level, nonblank message and
structured scalar fields. It returns `runtime-entry-accepted` only after the
local daemon durably records the entry. The integration client generates an entry ID for a call
and reuses it for a retry with the same body; conflicting reuse returns 409. A
queue-full or unavailable daemon error must be visible to the mission, never
reported as a successful entry. Authors are responsible for field content;
path exclusions or key-name filters cannot detect every secret.

The runtime attaches robot, run/execution, revision and initiating human context
from a verified, short-lived execution credential, not fields supplied by the
mission. That credential must be scoped to one running execution, delivered only
to its process, invalidated when that execution ends, and never appear in logs,
source, artifacts or the hosted event payload. The design must cover both
production runs and development executions, including which context is available
for each. Communication is local to the robot; the daemon persists the entry
before asynchronous replication to the Platform. The daemon's control listener
serves the endpoint. The optional integration client reads its runtime base URL from
`RINGFOUR_LOGBOOK_URL` and per-execution bearer from `RINGFOUR_LOGBOOK_TOKEN`,
both to be injected by the Runner. It posts only to the runtime route, rejects
redirects, and retries ambiguous transport failures with the same entry ID and
body. Without the runtime configuration it raises an error. Neither setting is
an Agent connection credential or a human token.

For the initial runtime, Runner derives a bearer for each unique execution ID
using HMAC-SHA-256 over `ringfour-logbook-v1:<execution-id>` and the existing
private Runner/daemon shared secret. The bearer is `<execution-id>:<hex-mac>`;
it grants only this runtime entry route. On every call the daemon verifies the
MAC in constant time, confirms through the private Runner API that this exact
execution is `running`, and resolves its operation from its own durable
execution mapping. The Runner injects the bearer and configured local daemon
URL into the mission process and removes the master Runner credential from the
process environment. Once execution ends, its token is rejected. On Runner
restart, interrupted executions become terminal and their tokens cease to work.
The URL is configured as `RUNNER_LOGBOOK_URL`; it must be reachable from Runner
on the robot and is not a public Platform endpoint. Trusted mission code is
assumed, and this token is not a security sandbox around the process.

The daemon retains a request fingerprint per entry ID for at least 24 hours and
while its outbox event is unacknowledged. Matching retry returns the original
acknowledgement, including after a lost response; conflicting payload or
execution returns 409. Once acknowledged and 24 hours old, replay records can
be collected. Retention is bounded to 8192 records; if the queue or replay
capacity is exhausted, new entries return 503 rather than reporting success.

The request permits at most 16 named fields; values are JSON scalars and strings
are bounded. Nested objects, arrays, arbitrary execution identity and extra
properties are rejected. The request limit is 8192 UTF-8 bytes. This is the
first proposed entry type, not a claim that other levels or richer fields exist.
The device event `mission.logbook.entry` carries the author payload, execution
ID and its associated durable start/restart operation ID. Its event ID is the
entry ID. PostgreSQL stores the execution ID and JSON payload separately from
the prototype state, checks that the execution and operation belong together
and to the robot, and derives the human actor and revision from the associated
hosted execution. Development executions refer to their restart operation and
do not imply a production revision. Existing operation-state prototype records
remain readable and are not emitted automatically. Local SQLite stores the
payload in the existing sequence stream and retains a bounded entry-ID/replay
record after acknowledgement, so a lost response cannot append the entry twice.
The outbox event limit is 12288 UTF-8 bytes, while the author request remains
capped at 8192 bytes. Strings cannot contain NUL, and numeric values must be
finite and no larger in magnitude than 9007199254740991; these limits prevent
entries accepted locally from becoming unstoreable on the Platform.

## Existing Agent audit interface and integration gap

The SDK's existing `robot.audit.query()` reads the Agent's in-memory ring buffer;
`robot.audit.tail()` is a live stream that does not replay earlier events and can
disconnect. Agent audit also writes rotating JSONL files, while the SDK query
does not read those files. Its `AuditEvent.client` is an Agent connection ID,
not automatically a Ringfour execution or human actor. Agent audit records can
include detailed calls and are broader than the motion outcomes requested here.
Consequently neither copying the full audit stream nor polling it can presently
guarantee complete, correctly attributed Logbook motion history. Inspect and
pin the actual Agent/SDK versions and design a durable, scoped handoff with the
Agent project before claiming this category works. Do not synthesize a verified
stop or infer ownership when the Agent has not supplied evidence.

## Existing ingestion prototype

`device-event.schema.json` describes an immutable event with a UUID, robot ID,
positive durable local sequence, occurrence time, type, schema version, operation
ID and an allowlisted payload. The existing prototype type is
`device.operation.state_changed`; its payload contains only the observed operation
state. It never includes the command, result, code, output, file paths, token or
error message. It was used to test transport; it is not an instruction to
automatically log every device operation. No producer is connected. Event occurrence is the daemon's clock; the hosted API assigns
`received_at` on ingestion. Local sequence, not wall-clock time, determines the
order for one robot. The event's robot must match the machine credential and its
operation must belong to that robot.

The machine actor is the authenticated robot; do not trust a device-supplied
human actor. The Platform can resolve an operation's originating user from the
existing `requested_by_user_id`. A null value means unknown or absent, not a
fabricated user. This event represents a machine observation, not the human
request itself. Separate human action and execution events still need to be
specified. Schema versions are explicit so old records retain their meaning.

## Ingestion and replay

Machine-authenticated `POST /v1/device/logbook/events` takes 1–100
events and a request body of at most 64 KiB. The machine credential, not a
client-supplied organization, scopes the batch. Every event belongs to that
robot and the batch must be strictly contiguous and ordered. Invalid events
return 400; oversized requests return 413; mismatched ownership, gaps or
conflicting duplicate IDs/sequences return 409. Errors use the shared envelope.
Unsupported event types or versions are invalid, not silently discarded.
The proposed error codes `sequence_gap` and `event_conflict` distinguish a
missing sequence from conflicting reuse; their representative envelopes are
in the Logbook fixtures. A wrong robot or operation is `event_conflict`.

The Platform serializes ingestion per robot and atomically stores the entire
batch and its checkpoint. Identical already-stored events are accepted on replay,
including a duplicate prefix followed by new contiguous events. Reusing an event
ID or sequence with different event content is a conflict. A batch beginning
past the next sequence is a gap; no part of a failed batch commits. The response
contains the highest contiguous committed `acknowledged_sequence` for that
robot, even if the submitted batch was an earlier replay. A lost response is
retried with the same events and IDs. Sequence starts at 1 and is never reset
or reused for an enrolled robot's persistent event store. Recovery after losing
that store is unresolved and must not silently reuse a prior sequence.
Replay compares validated event fields, with UTC timestamps compared as
instants rather than by their textual fractional-second representation.

## Persistence and implementation boundaries

The intended daemon SQLite outbox stores immutable event content and sequence
alongside a durable next-sequence counter and last-acknowledged checkpoint. A
state mutation and its corresponding event must commit in the same transaction;
replay of the same state must not create another event. An acknowledged event
can be removed only after the local checkpoint is committed. The Platform needs
an append-only PostgreSQL event table with unique event ID and unique
`(robot_id, local_sequence)`, plus a per-robot ingestion checkpoint. Its query
indexes and retention need to be specified alongside the query contract. Events
must be scoped through the authenticated robot's organization.

For the hosted ingestion slice, PostgreSQL `logbook_events` stores the
event ID, robot ID, operation ID, local sequence, event type and version,
occurrence and receipt times and allowlisted state; a unique index on
`(robot_id, local_sequence)` and primary key on event ID prevent duplicate
history. `logbook_checkpoints` has one row per robot and its last contiguous
sequence. Both tables reference the existing robot; the event also references
the operation. A robot row lock serializes batches and credential rechecks.
These tables are additive to the starter schema, have no automatic deletion yet,
and do not backfill earlier execution or audit records.

The bounded disk policy and backpressure behavior remain **incomplete**. The
current optional Agent producer never waits for journal storage on a stop path,
but it can lose terminal events when its queue fills; it emits an explicit gap
marker rather than claiming completeness. It does not yet satisfy the milestone
requirement to preserve terminal events under outbox pressure. A bounded
reservation/reconciliation strategy and behavior for actual disk exhaustion
still need design and tests. Similarly, retention, legacy event import, query cursor
semantics, and actor attribution for actions not represented by a hosted
operation remain open. Do not claim complete or retroactive history until those
are resolved and end-to-end evidence exists.

### Initial daemon outbox slice

The daemon outbox is a persistent SQLite queue of at most 4096 events,
with at most 12288 UTF-8 bytes per event and batches of at most 100 events / 64
KiB. This is a logical payload bound, not a filesystem quota: SQLite page,
index, journal and other database overhead must be provisioned separately.
`logbook_outbox_state` persists the robot binding, last allocated sequence and
highest acknowledged sequence even when the queue is empty. Event insert and
sequence allocation share the caller's transaction. A full queue raises an
explicit capacity error and leaves that transaction unchanged. The delivery loop
only removes events after validating a successful Platform checkpoint; a lost
response or restart replays the same IDs and content. Invalid or impossible
acknowledgements leave the queue intact. Re-enrollment as another robot does not
reset or rebind an existing stream.

**No automatic operation producer is connected yet.** In particular, do not
call the bounded append function from an automatic lifecycle until the
reservation policy ensures stop/cleanup operations cannot be blocked by full
history. Mission-author calls return an explicit error when capacity is full.

## Console and device demonstration

Logbook is a first-class Console destination with its own sidebar item. Its
view must use the authorized organization-scoped history API rather than test
fixtures, and expose entry content and available robot, execution and actor
context. The initial history query and Console sidebar/detail view are implemented. A test
mission must exercise the shipped capabilities using the separately installed
runtime package and the real SDK, without assuming that a lifted robot's sensor
readings or odometry change; physical movement is not required to verify authored
entries. Automatic motion examples must wait for the Agent handoff and report
only events actually observed.

### Initial history query

`GET /v1/organizations/{org_id}/events` requires operator membership and
returns `history-page` entries, scoped by the robot's immutable organization.
Optional filters select robot, execution, operation, originating actor, event
type and occurrence-time bounds (`from` inclusive, `to` exclusive). Results
are ordered by Platform receipt time and event ID ascending; the opaque cursor
continues after the last result. Local sequence is the authority for ordering
within one robot, not a claim that device clocks agree. A client must keep the
same filters when following a cursor. Limits are 1–100, default 100. Actor is
resolved from the associated durable operation and can be null. Author payloads
come from the stored JSON; prototype state events expose their state only.

## Proposed Agent motion handoff

The `agent-motion-event` schema is a new cross-project design, not an existing
Agent RPC. It defines stable event and stream IDs, per-stream sequence, explicit
accepted/refused/completed/cancelled/verified-stop/guard-trip categories, and
nullable Agent operation, session and owner attribution. Only an Agent-confirmed
stop may set `stop_verified: true`; do not convert a cancellation, a dispatch
acknowledgement, or an absent response into a verified physical stop. Continuous
velocity updates are excluded. A null owner or operation is genuinely unknown;
the Platform must not infer a human or mission from an Agent connection number.

The actual Agent command ledger records command claims and task snapshots and
can identify an authenticated principal, but it currently expires records and
does not expose an acknowledged event stream. Agent task terminal records and
stop/guard evidence are distinct sources; handler success alone does not imply
physical completion. Existing Agent audit messages contain many calls and can
contain parameters, so blindly forwarding the audit stream is out of scope.

The production handoff still needs a defined, authenticated reader and durable
acknowledgement protocol, bounded storage and explicit gap reporting. It must
work with an absent Platform, be opt-in for standalone Agent use, and never
block or delay a safety stop on storage or network I/O. Before wiring any
producer, specify how capacity reserves terminal outcomes for already admitted
work, how guard/stop events are recorded after physical stop attempts, how an
unclean restart or disk exhaustion is reported, and how independent event
streams map into the daemon's sequence. Implement and test these invariants in
the Agent, daemon and Platform together; a schema fixture is not a handoff.

### Agent-local journal foundation

The initial Agent-side journal is opt-in and separately stored in the Agent's
persistent state directory. A SQLite singleton records a generated stream UUID,
last allocated sequence and acknowledged sequence. Immutable rows store only
`agent-motion-event` fields; sequence and event append commit together under an
immediate transaction. A bounded queue rejects appends explicitly rather than
silently overwriting records. Reading after a cursor returns contiguous events;
requesting an acknowledged/removed range or a future cursor returns an explicit
gap/invalid-cursor error. Acknowledgement is monotonic, limited to the journal's
current sequence and deletes only acknowledged rows atomically; it rejects an
acknowledgement across missing rows. Reads use a consistent SQLite snapshot.
The stream ID
changes if the journal is replaced, making replacement visible to a reader.
The recorder submits selected motion events through a bounded in-memory queue.
It must not call the journal synchronously from a physical stop path. A full
queue can lose records; the persisted dirty-boot marker and
`motion.history_incomplete` event make that loss visible instead of claiming a
complete history. It does not reserve guaranteed terminal-event capacity.

### Agent-to-daemon local transfer (new design)

When explicitly enabled, the Agent exposes a dedicated Unix stream socket for
the motion journal. It is separate from the robot-control socket, uses kernel
peer credentials to admit the Agent's local UID, and is never available
on TCP. Its newline-delimited JSON requests have a strict size limit and only
two methods: `read` (`stream_id`, `after`, `limit`) and `acknowledge`
(`stream_id`, `sequence`). Responses are either `{"ok":true,"result":...}`
or `{"ok":false,"error":"..."}`. A reader must treat a changed stream, a
discarded range, malformed data, and an unavailable journal as errors rather
than resetting its cursor. Acknowledgement deletes only contiguous saved rows.

The daemon copies each page and its Agent stream checkpoint into its own
bounded SQLite inbox in one transaction before acknowledging that checkpoint
to the Agent. Repeating a read or acknowledgement after a lost reply is safe;
inbox exhaustion leaves the Agent records unacknowledged. This transport does
not create events or run from any motion or stop path.

### Agent motion events in Platform history (new design)

The daemon moves inbox rows, in Agent sequence order, to its existing robot
outbox transactionally. A row is removed only in the transaction that allocates
the robot-local event sequence and inserts its outbox event. Full outbox or
missing identity leaves the inbox intact. Each envelope has type
`agent.motion.event`, the original event UUID and occurrence time, a null
Platform operation ID, and the complete validated Agent motion event as payload.
The Agent's operation and owner are opaque local attribution; neither is a
Platform operation, execution or authenticated human. Platform ingestion uses
the existing authenticated robot credential, ordered sequence, replay and
organization-scoped history. It records null Platform operation, execution and
actor IDs for these events instead of guessing. Console displays the Agent event
type and payload. This design does not assert a physical stop absent explicit
Agent verification.

### Agent producer loss reporting (new design)

The optional producer uses a bounded in-memory queue and a separate writer;
enqueueing never waits for disk or network. Agent startup persists a dirty-boot
marker before enabling production. An unclean shutdown, queue overflow or
writer failure may lose events, so the journal emits an explicit
`motion.history_incomplete` marker when it can write again. While the marker
is pending, later events are withheld; the marker records a gap rather than
inventing missing history. A clean shutdown clears the dirty marker only after
the queue and any gap marker are durable. When journal storage is full, the
writer retries and the Agent's stop path continues independently. A successful
measured stop criterion is separate from hardware safety commissioning.

Agent opt-in uses `RINGFOUR_AGENT_MOTION_SOCKET` or
`--motion-handoff-socket`, and stores the journal in the persistent Agent state
directory. The socket's parent directory must already exist, be owned by the
Agent UID and have no group or other permissions; the Agent and daemon need
the same UID and a shared mount for that directory. The daemon enables its
reader with its own `RINGFOUR_AGENT_MOTION_SOCKET`. A missing or replaced journal
after acknowledgement is a permanent gap requiring operator investigation,
not an automatic reset.
