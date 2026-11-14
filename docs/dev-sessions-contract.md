# Development sessions v1 — proposed Ringfour design

Status: **partially implemented; full milestone acceptance pending**. These interfaces
are new Ringfour design, not SDK or Agent APIs. A physical Balena check for 06
is deferred and remains an incomplete external gate. Development sessions do
not publish or change production revisions or selected deployments.

## Session and authorization

A developer or owner creates a session with `POST /v1/robots/{robot_id}/dev-sessions`
and `{ "ttl_seconds": 3600 }`; the allowed range is 300–28800 seconds. The
server records the authenticated local user as owner and computes an immutable
expiry from its own clock. At most one nonterminal session per robot is allowed.
Creation and restart require an online robot and return 503 `robot_unavailable`
when offline. They never preempt another execution. Creation returns 202 with
session and durable operation IDs, not a promise that a workspace exists.

Members with operator access may read a session in their organization. Sync
and restart require the session owner to retain developer access, or an
organization owner; a different developer is not automatically the session
owner. The session owner, an organization owner, or an operator can request
closure. Inaccessible objects return 404 and insufficient rights return 403.
Every mutation uses `Idempotency-Key`, scoped as in architecture, retained at
least 24 hours. Identical replay returns the original acceptance IDs; different
payload with the same key returns 409 `conflict`. An active session or
production execution blocks competing starts with 409 `execution_conflict`.

## Local data path — revised design

The Platform is the control plane: it authorizes the human, coordinates the
session, exchanges connection information and records small state/operation
metadata over the robot's existing outbound connection. Development source,
live stdout/stderr and artifacts travel directly between the CLI and the
robot's daemon on the local network. Runner remains private behind the daemon.
There is no automatic hosted relay or cloud source archive for this fast path;
if the workstation cannot reach the daemon locally, the CLI reports that it
cannot connect. Production mission uploads keep their existing hosted flow.

### Connection and grant proposal

The daemon generates a self-signed TLS certificate and private key on first
startup, stores them in its persistent volume with owner-only permissions,
and announces the SHA-256 fingerprint of its DER certificate and its
configured LAN address using machine-credential-authenticated HTTPS
`PUT /v1/device/local-dev-endpoint`. The Platform
binds that address and fingerprint to the authenticated robot, never to an
untrusted client-provided robot ID. If no routable address is configured, the
CLI accepts an explicit `--daemon-url`; it still obtains the certificate
fingerprint through the authenticated Platform. Automatic discovery is deferred.
Use a dedicated configurable HTTPS listener, separate from the unauthenticated
status page and the private Runner; do not reuse either listener's credentials.

The authenticated CLI generates 32 random bytes and keeps the base64url token
only in memory. It sends the SHA-256 digest of those bytes to the Platform in
`POST /v1/dev-sessions/{id}/local-grants`, together with a unique grant ID. The
Platform authorizes session owner/developer or organization owner, checks the
robot is connected and session is open, caps grant expiry at the earlier of
five minutes or session expiry, and sends a `dev.session.grant` command over
the robot-authenticated connection. The daemon durably stores only the hash,
session, grant ID, expiry and allowed scopes (`source` and `output`). The API
returns the operation ID, optional daemon URL and certificate fingerprint; the CLI waits
for successful grant installation before connecting. It never sends the raw
local token to the Platform. A matching idempotent retry reuses the grant
operation; conflicting key or grant ID reuse returns 409. A lost CLI token
requires a fresh grant. Grant material must not enter logs or project context.

The CLI opens HTTPS directly to the LAN daemon and verifies the exact DER
certificate fingerprint from the authenticated Platform before sending a
bearer token. A fingerprint mismatch, expired certificate or unavailable
address fails closed; never silently fall back to an unpinned connection or a
hosted relay. The daemon compares the SHA-256 of the presented token in
constant time against a stored, unexpired grant for the requested session and
scope, and denies closed or expired sessions. Limit each session to one active
grant; installing another invalidates the previous one atomically. Closing a
session or rotating its device credential revokes its grants. If the daemon is
offline when a revocation is requested, existing grants expire within five
minutes and the robot must process pending revocation on reconnection. The grant
is not a human Auth0 or Runner credential. Certificate rotation must first be
reported over an authenticated device connection; clients re-fetch the pinned
fingerprint before reconnecting. The precise certificate installation and
rotation implementation remains to be tested with Balena persistent volumes.

The local API exposes `PUT /v1/local/dev-sessions/{id}/source` for source,
and session-scoped `GET /v1/local/dev-sessions/{id}/executions/{execution_id}`
and `/output` for status and bounded stdout/stderr. These require the local
bearer grant. The Platform never receives source or output; the daemon reports
only execution state, exit code and bounded errors. The CLI supports `dev start`,
`sync`, `restart`, `logs`, `status` and `stop`; its visible project context
stores session and robot IDs, never the local grant. Artifact access,
credential-rotation revocation and bounded grant cleanup are not implemented.

## Source sync

The proposed local source request uses `source-request`: the base generation,
a base64-encoded ZIP, its exact byte length and SHA-256, and a full manifest
describing every file in the resulting workspace plus explicit `deleted_paths`.
Generation 0 denotes the initially empty workspace. The ZIP is a complete
snapshot, not a patch. Each regular file must appear exactly once in both the
archive and manifest with matching path, byte length and SHA-256. The explicit
deletion list must equal prior committed paths absent in the new manifest and
is empty for generation 0. Reject duplicates, overlap, incorrect deletion
lists, digest mismatches and malformed encodings as `invalid_source`. Check
the base against the last committed generation under a session lock: a mismatch
is `stale_generation`; another pending sync is `sync_in_progress`. A client
must read current state and resubmit after a conflict.

Limits match 05: 1 MiB compressed and extracted total, 256 KiB per file, and
1024 regular files and 2048 ZIP entries. A session retains at most 20
committed generations; further syncs return `capacity_exceeded` until the
session is closed. Paths use the 05 restricted relative POSIX alphabet;
reject empty, `.`, `..`, absolute, duplicate, link and special-file entries.
The CLI excludes hidden files and directories, including `.git`, `.env`,
`.venv`, and `__pycache__`; the daemon and Runner reject those entries even if
manually sent. This is a path-based rule, not detection of every secret a user
could put in source. `requirements.lock` and a `.py` entrypoint must exist; the
lock's SHA-256 must match `lock_sha256`. File and path comparisons are
case-sensitive. Enforce limits, extraction and archive verification at both
the daemon and Runner.

The daemon receives and verifies the snapshot on the LAN and stages it for the
private Runner. Runner checks the base, bytes and manifest, writes a temporary
generation and atomically commits only after all checks. The local PUT is
idempotent by session and key; repeat the same key and body after a lost response
or `503 sync_pending`. A different body with the same key returns 409. A 200
returns `sync-result` only after Runner commits; this does not imply Platform
reporting has completed. The authenticated local session check includes the
latest local generation and `report_pending` count. The local source-state GET
returns the current generation, file paths and archive hash to construct explicit
deletions and avoid creating a generation for an unchanged snapshot.

The daemon records pending syncs and committed reports in its SQLite outbox;
it reuses the same Runner operation after interruption and reports committed
generations in order to the machine-authenticated
`POST /v1/device/dev-sessions/{id}/generations`. Reports contain operation and
grant IDs, base and new generation numbers, archive size and digest, and lock
digest; they never contain source bytes, filenames or a manifest. The Platform
records each report atomically and accepts identical replays. When offline, the
local generation remains authoritative and the hosted generation may lag. A
failed transfer leaves the previous generation usable. Old unreferenced staging
files are collected after five minutes; committed generations remain subject
to the 20-generation session limit.

## Restart, expiry and recovery

A sync never stops or starts code. `POST /v1/dev-sessions/{id}/restart` takes the requested `generation` and requires
it to match the latest generation reported by the daemon; generation 0, pending sync, or closing sessions
return 409. A restart may stop this session's own previous execution, but must
not preempt production or another session. It reserves a distinct execution ID
and returns 202; process start is observed through existing execution status.
`dev.session.restart` carries the `runner-restart` fields through the durable
device operation, and private Runner `POST /v1/dev-sessions/restart` accepts the
same schema and Runner authentication. The actor is the requesting human.
Uncertain interrupted executions are marked `interrupted_unknown_outcome` and
never automatically rerun. A replay must not launch the reserved ID twice.

For the private Runner restart operation, `runner-restart` includes `robot_id`
for local execution attribution as well as the stable operation, session,
generation and execution IDs. The Runner verifies that the generation is the
latest committed snapshot, stops only this session's previous execution, and
reserves the new ID using its existing single-active-execution guard. Replay
returns that same execution; if its outcome is unknown after Runner restart it
is failed, not run again. A conflicting production execution is never stopped.
Closing a session must stop its own active execution before deleting workspace
or environments. These are new Runner contracts, not SDK calls.

Runner stores code generations and environments separately on its own volume.
The environment identity includes the lock digest, Python interpreter and
platform. Source-only changes use the existing matching environment without
running dependency installation. For a changed lock, prepare a new uv
environment before selecting it for execution; on failure retain the previous
generation/environment and report `preparation_failed`. Production cache and
active production data must not be deleted by session cleanup. Selection of the
execution generation happens only after its environment is ready. Persistent
workspace and environment survive daemon and Runner restarts; no code is a
security sandbox.

`DELETE /v1/dev-sessions/{id}` records closure even while offline and reports
`closing` until local cleanup is acknowledged. Closure stops the session's own
active development execution before removing its workspace. Expiry uses the same durable stop/cleanup workflow
and never implies that an offline robot has stopped. A session cannot sync or
restart after expiry or closure begins. Runner cleanup retries on reconnect and
removes only this session's paths; it must finish within 24 hours of successful
reconnection, subject to persistent failures surfaced as errors. Terminal
`closed` or `expired` requires confirmed cleanup. Retain hosted session,
generation metadata and operation history for 30 days after closure, then
remove session metadata with a retrying sweeper. This retention sweeper is not
implemented yet. Do not reuse a
session ID. Development-session output is read locally; its retention limits remain to be specified.

State transitions: `creating → ready | closing | failed`; `ready → syncing |
starting | closing`; `syncing → ready | running | closing | failed`; `starting
→ running | ready | closing | failed`; `running → syncing | starting | ready |
closing | failed`; `failed → closing`; `closing → closed | expired`. A sync
while running returns to `running`; completion of a run returns to `ready`.
`stopping` denotes the stop phase of an explicit restart and transitions to
`starting | ready | closing | failed`. Terminal states do not reverse. Status
includes latest committed and active generations separately; active generation
is null before a successful start. Runtime execution state remains authoritative
for whether a process is running. `last_error` is bounded and secret-free.

## Persistence and concurrency

PostgreSQL `dev_sessions` records session, robot, owner, state, committed and
active generation, expiry, latest operation/execution IDs, error and timestamps;
a partial unique index enforces one nonterminal session per robot. A robot row
lock serializes creation, sync/restart/closure and competing production starts;
the existing active-execution uniqueness rule remains authoritative.
`dev_session_generations` stores session/generation, archive digest,
size and pending/committed/failed status. No hosted source object is needed. `(session_id,
generation)` is unique among pending and committed rows; failed attempts retain
their operation IDs and may coexist with a retry of the same generation. An operation-linked pending candidate is distinct from
the committed generation and is not exposed as usable. Operation records and
idempotency records retain workflow intent and actor; sync completion updates
candidate and session atomically. Failed candidate metadata is retained for diagnosis until bounded cleanup. Daemon SQLite uses the
existing durable operation log; Runner persists session metadata, operation
replay records, immutable generation directories and separately keyed
preparation environments. Reconciliation checks existing operation results
before any retry. Device and Runner schema fixtures are proposals and must be
updated together with consumers when implemented. A follow-up migration removes
the superseded hosted object key and file manifest from PostgreSQL; file names
and contents stay on the workstation and robot.

The `dev.session.create`, `dev.session.close`, and `dev.session.expire` device
commands and private Runner `POST /v1/dev-sessions/lifecycle` use the
`lifecycle-command` schema; reason must agree with the command kind. A creation
result confirms an empty generation 0 workspace and its expiry; close/expire
succeeds only after the session execution has stopped and local session paths
are removed. The `sync-result` schema specifies the successful sync result.
All device messages use the existing v1 envelope, durable operation IDs and
acknowledgement/replay rules. Runner endpoints require the existing private
bearer secret, never a human token. Failed operation results use the existing
versioned operation error envelope, and transport errors never imply success.

Representative errors follow the shared envelope:

```json
{"error":{"code":"stale_generation","message":"The base generation is stale.","request_id":"example"}}
```

```json
{"error":{"code":"execution_conflict","message":"Another execution is active.","request_id":"example"}}
```
