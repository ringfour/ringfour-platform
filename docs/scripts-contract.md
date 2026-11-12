# Script execution v1 — proposed Ringfour design

These are new Ringfour interfaces, not claims about an existing SDK or Agent.
Implemented behavior and acceptance results are recorded in `docs/evidence/05.md`.

## Revisions and upload

An owner or developer creates a revision for an organization with
`POST /v1/script-revisions`. Its immutable metadata contains the organization,
SHA-256 and byte length of the uploaded archive, the Runner canonical bundle
digest, the Python entrypoint, Python requirement and dependency lock. An
upload is not a usable revision until finalization has independently read and
verified the stored bytes. Limit an archive and its extracted bundle to 1 MiB,
each extracted file to 256 KiB, and each bundle to 1024 regular files. Reject
absolute paths, duplicate paths, `.` and `..` components, symlinks and special
files. The entrypoint must be a `.py` file inside the bundle. Preserve the
canonical manifest algorithm already defined in `runner-contract.md`.

The archive format is ZIP. File paths use only ASCII letters, digits, `_`,
`-`, `.`, and `/`; every component is nonempty and is neither `.` nor `..`.
The bundle contains `requirements.lock`, a UTF-8 dependency lock whose SHA-256
matches `lock_sha256` (an empty lock represents no third-party dependencies).
The lock is included in the canonical bundle digest. No ZIP entry may reuse a
path; directories must not collide with files. A ZIP's declared or actual
uncompressed size cannot exceed the stated per-file and bundle limits.
For script runs the private Runner start request adds `python_requirement` and
`lock_sha256` together. Runner rejects an incompatible Python interpreter or a
mismatched lock and prepares the environment with `uv pip sync --require-hashes`
against `requirements.lock`. An empty lock installs no external packages. The
existing diagnostic requests omit both fields and retain their behavior.

Initiation returns an ID and a short-lived scoped upload URL. Finalization
reads the upload once into a bounded private buffer, checks its actual
size and SHA-256, validates extraction, entrypoint and the canonical digest,
then stores those same verified bytes under a new server-controlled immutable
key. It does not validate one version and copy a mutable object afterwards.
This promotion is required because an upload URL can remain
usable after finalization and Garage does not provide object versioning. Never
trust user-provided object metadata as verification. Lost finalize responses
must be retryable without creating a different revision; a conflicting replay
returns 409. Expired incomplete uploads and their temporary objects require
bounded cleanup. A database row for an incomplete upload must not imply that a
usable revision exists.

`POST /v1/script-revisions` requires an `Idempotency-Key` (1–200 characters)
scoped to the authenticated user, route and organization. A matching replay
returns the original revision and upload URL; a changed request returns 409.
The URL is the API path `/v1/script-revisions/{id}/content`, authorizes the
same organization role using the caller's human bearer token, accepts one ZIP
body with `PUT`, and expires 15 minutes after creation. It never exposes object
store credentials. `POST /v1/script-revisions/{id}/finalize` checks the upload
and returns the ready revision; a successful repeat is idempotent. Failed
verification returns 400 and does not publish the revision; missing uploaded
bytes returns 409. The upload URL refuses writes once finalized or expired.
At most 100 pending plus ready revisions may be retained per organization;
capacity exhaustion returns 503. Records and immutable data are retained until
operator cleanup; expired incomplete objects are removed by a durable retrying
sweeper 24 hours after their upload expiry and remain counted until removed.
The same sweeper removes temporary upload copies for ready revisions, recording
cleanup completion for retry. Ready revisions and execution histories are not
automatically deleted.

PostgreSQL `script_revisions` records the UUID, organization, creator, declared
archive digest/size, canonical bundle digest, entrypoint, Python requirement,
lock digest, upload expiry, state (`uploading` or `ready`), timestamps and
idempotency key/fingerprint. The immutable object key is server-derived. A
unique constraint on creator, organization and idempotency key serializes
concurrent retries. Organization and creator are restrictive foreign keys.
Finalization serializes updates to a revision and commits `ready` only after
the verified object is written. An orphaned final object after a crash is safe
to overwrite only with the same verified bytes on retry.

The private storage interface supports a filesystem implementation for local
development and a configurable S3-compatible backend for Garage. Do not depend
on S3 ACLs, bucket policies or object versioning. Endpoint, region, bucket and
credentials are deployment configuration, not robot-visible credentials. API
and device-accessible URL origins may differ and must be configured explicitly;
never give a device a URL containing an internal Docker-only hostname.
The API's internal object-store boundary accepts server-generated relative keys
only, reads and writes at most 1 MiB per object, and supports put, bounded get,
and idempotent delete. It does not grant clients direct access to immutable
keys. Storage failures are not translated into successful finalization.
The internal adapter is registered lazily through `Storage:Backend` (either
`filesystem` or `s3`). The former requires an absolute `Storage:FilesystemRoot`;
the latter requires `Storage:S3:Endpoint`, `Storage:S3:Region` and
`Storage:S3:Bucket` and obtains credentials from the AWS SDK's standard provider
chain. The S3 adapter uses path-style addressing and disables chunked upload
encoding, while retaining request signing. Use TLS for a hosted S3 endpoint.
API readiness checks both PostgreSQL and the configured private storage. A
missing or unreachable storage configuration makes readiness fail; liveness
does not contact storage.

Errors use the shared `{error:{code,message,request_id}}` envelope. For
example, starting a new run while disconnected returns HTTP 503 with
`{"error":{"code":"robot_unavailable","message":"The robot is offline.","request_id":"example"}}`.
An overlapping active script returns HTTP 409 `execution_conflict`; a
conflicting idempotency replay returns 409 `conflict`; a stopped or failed
execution is reported separately from the initial 202 acceptance. Storage
unavailability returns 503 `storage_unavailable` without exposing credentials.

## Execution and retrieval

`POST /v1/robots/{robot_id}/script-runs` requires developer access to the
robot's organization and an immutable revision belonging to that organization.
It returns 202 with stable operation and execution UUIDs. Request idempotency is
scoped to the user, route and robot, and replay of the same payload returns the
original IDs; a changed payload returns 409. A disconnected robot returns 503
without creating a new operation. Durable delivery after acceptance uses the
existing device operation and outbox rules. A transport reconnect must not
launch a second process.

Runner state remains `queued → preparing → running → succeeded | failed |
cancelled`; terminal states never reverse, and unverifiable in-flight work after
a Runner restart fails with `interrupted_unknown_outcome`. At most one execution
may be active per robot. Start never silently preempts work. An operator may
stop an execution; output and artifacts are organization-scoped. Output uses
independent stdout/stderr byte offsets and bounded chunks; no code may infer
completion merely from operation acceptance. A script's actor is the requesting
human user, retained even when the device reports its local lifecycle events.

Artifacts are regular files under an execution-specific output directory. Reject
links, path traversal, devices and files that escape that directory. Artifact
metadata includes size and SHA-256; hosted metadata becomes downloadable only
after upload and verification. The OpenAPI and JSON Schemas define request and
response shapes; limits, expiry and retention are specified above and below.
Interruption, authorization, offset replay, storage unavailability and device
restart checks are recorded in the evidence.

### Private Runner artifact interface (new design)

Runner sets `RINGFOUR_ARTIFACT_DIR` to an execution-specific directory before
launching the Python process. Once the process has exited and its pipes have
been drained, Runner snapshots regular artifact files into its own execution
storage before publishing terminal state. Names are relative POSIX paths, at
most 240 UTF-8 bytes, with no empty, `.` or `..` components, control characters
or backslashes.
Symlinks and special files are rejected, including in parent directories. The
snapshot has at most 16 files, each at most 256 KiB and at most 1 MiB total.
If collection fails or a limit is exceeded, the execution is failed with
`artifact_collection_failed`; a cancellation remains cancelled and reports the
collection error separately. No partial artifact set is exposed. Runner retains
the snapshot with the execution, including across restart.

The existing private Bearer authentication applies to both endpoints.
`GET /v1/executions/{execution_id}/artifacts` returns `{execution_id, artifacts}`;
each entry has `path`, `size` and lowercase SHA-256 `sha256`. It returns 409
`execution_not_terminal` while the process or snapshot is active. The data
endpoint is `GET /v1/executions/{execution_id}/artifacts/content` with query
parameters `path`, integer `offset` (0–262144), and integer `limit` (1–16384).
It returns `{execution_id, path, offset, next_offset, data_base64, eof}` with
offsets measured in bytes. Missing executions or artifacts return 404;
malformed paths or ranges return 400. Content and listing are snapshots, not
live views of a mutable script directory.

### Hosted execution synchronization (new design)

The Platform must durably record each run, its requesting user, robot and
revision, and a stable dispatch operation in one transaction. The daemon must
commit the sync intent before starting Runner. Runner continues to own the
process, output and artifact snapshots; the daemon may restart without causing
a new execution ID or replaying a process. A reconnect resumes transfer from
persisted byte offsets and idempotent observed-state reports. The machine credential is checked on
every device transfer and its robot identity must match the run. Human queries
derive organization authorization from the run's robot and never use that
machine credential.

Transfer operations follow these replay rules: a repeat
of the same stream offset and bytes is accepted; conflicting bytes or a gap are
rejected. Terminal execution states are immutable, and a lost response cannot
create a second artifact or lifecycle event. The durable hosted record must not
report artifact availability before its full bytes and digest have been
verified in private storage. Run submission must fail explicitly when the
target is disconnected rather than silently create a queued run.

`POST /v1/robots/{robot_id}/script-runs` accepts `{revision_id}` and requires an
`Idempotency-Key` as described above. The returned operation ID is also the
execution ID. Store the run and its dispatch operation in the same database
transaction. A partial unique index permits only one nonterminal run per robot;
retain at most 100 runs per robot until explicit operator cleanup. Check the
current authenticated device session before accepting a new run; matching
idempotent retries return the original result even if the device is now offline.

Human readers use `GET /v1/executions/{id}`,
`GET /v1/executions/{id}/output?stream=stdout|stderr&offset=N&limit=N`, and
`GET /v1/executions/{id}/artifacts`. An artifact's bytes are served by
`GET /v1/executions/{id}/artifacts/content?path=...` with the same authenticated
organization scope. Operators may request a stop with
`POST /v1/executions/{id}/stop` and an idempotency key; acceptance does not
assert that the process has stopped. Output is retained in bounded PostgreSQL
byte arrays, at most 64 KiB per stream, and read chunks are at most 16 KiB.

The daemon fetches the immutable bundle through a machine-authenticated
`GET /v1/device/executions/{id}/revision` scoped to its own robot and a
nonterminal run. It verifies the archive digest, safely extracts it, verifies
the canonical manifest, and launches Runner with the same execution ID.
`PUT /v1/device/executions/{id}/output/{stream}?offset=N` appends at most 16 KiB
at the exact current offset. A replay that matches already persisted bytes is
accepted; an overlap with different bytes or a gap is 409. API and daemon
persist offsets before advancing, so a lost response never loses data.

After Runner is terminal the daemon snapshots each bounded artifact, sends it
to `PUT /v1/device/executions/{id}/artifacts?path=...&sha256=...`, and retries
until acknowledged. The API computes the digest itself, stores verified bytes
under a server-derived key and commits metadata; an identical repeat is
accepted and a changed same-path repeat is 409. An object written before a
database failure can be safely rewritten by retry. Finally the daemon sends a
terminal status; all output and artifacts must already have been acknowledged.
Before then, status updates may report `queued`, `preparing` or `running`.
Once terminal, only the same terminal status and metadata may be replayed.
Daemon SQLite `script_sync` records execution ID and output offsets before
starting Runner, and marks completion only after terminal acknowledgement.
Runner records are the durable source of local execution status and output.
Transient device/API or Runner outages retry without starting a second process.
An accepted start whose acknowledgement is ambiguous stays pending beyond the
start deadline until the daemon can query Runner by the stable execution ID;
it never relaunches an existing execution. If no process exists after the
deadline, the device reports failure rather than starting it late.
Each accepted change of observed execution state also inserts a durable hosted
event in the same PostgreSQL transaction, attributed to the requesting user.
Duplicate state reports do not produce duplicate events. These basic events
record what the Platform observed; the full device event replication and
Logbook queries remain milestone 08 work.
