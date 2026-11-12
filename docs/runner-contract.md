# Runner v1 contract and persistence — milestone 04

These interfaces are **new Ringfour design**, not existing SDK or Agent APIs.
Milestone 04 ships only the two fixed diagnostic Python fixtures; there is no
user upload or dependency installation from untrusted requests. The code is
trusted; virtual environments isolate dependencies, not hostile programs.

## Private interface and staging

Runner listens only on the private device network. Every `/v1/executions` request
requires `Authorization: Bearer <runner-shared-secret>`, configured separately
in the daemon and Runner by deployment; secrets never occur in operation
payloads or logs. Missing/incorrect credentials return 401. Health endpoints
remain unauthenticated for local readiness. Deployment must supply a random secret;
there is no built-in default. A missing Runner secret makes readiness fail. Staged code is mounted read-only for Runner.

`POST /v1/executions` accepts `execution_id`, enrolled `robot_id`, lowercase SHA-256 `revision_digest`,
relative `code_ref`, and relative `entrypoint`; these identify a staged bundle.
The request body is limited to 4096 bytes. A bundle is a directory of regular files; its digest is SHA-256 of a canonical
manifest: UTF-8 JSON array of `[relative POSIX path, sha256 file bytes]` pairs,
ordered by path, compact JSON separators, no newline. Symlinks, path traversal,
non-regular files, files over 256 KiB, bundles over 1 MiB, and more than 1024 files are rejected.
`entrypoint` must name a Python file inside the bundle. Runner copies validated
bytes to its own volume and verifies the copy before execution. The caller must
not mutate staged content during validation; mismatch fails closed.

Identical start replay for an existing ID returns its current execution; a
changed request for the same ID returns 409. A second nonterminal execution
returns 409 `execution_conflict`; there is no preemption. `GET /v1/executions/{id}`
returns the execution. `POST /v1/executions/{id}/stop` is idempotent for the
same execution; terminal results are immutable. `GET
/v1/executions/{id}/output?stream=stdout|stderr&offset=N&limit=N` returns
base64 bytes with the next byte offset, EOF and truncated flags. Limits: 64 KiB
per stream per execution, read chunks at most 16 KiB and offsets at most 65536; bytes beyond the cap are
drained and discarded with `truncated=true`. Stop sends TERM to the process
group and escalates to KILL after 3 seconds. Preparation uses a per-revision uv
virtual environment and no network dependency installation in this milestone.

State is `queued → preparing → running → succeeded|failed|cancelled`; stop may
cancel queued/preparing. Failure includes a machine-readable reason; process
nonzero exit is failed. On Runner restart nonterminal work whose process cannot
be verified is marked failed `interrupted_unknown_outcome`; never relaunch it.

## Diagnostic hosted operations

The existing authenticated operation endpoint gets three new version-1 kinds:
`diagnostic.execution.start` with `{fixture: "hello"|"long"}`;
`diagnostic.execution.inspect` with `{execution_id, stream, offset, limit}`;
and `diagnostic.execution.stop` with `{execution_id}`. The start operation ID
is the execution ID. Start and inspect require developer, stop requires operator.
Start returns a Runner execution snapshot in its operation result; inspect returns
snapshot plus one output chunk; stop returns the resulting snapshot. Runner
validation, not a caller-supplied path, chooses the locally staged fixed fixture.
Runner 409 overlap is mapped to a failed hosted operation with `execution_conflict`; other Runner rejections map to stable, redacted error codes. All results are bounded to fit the 64 KiB device message limit. The existing
idempotency and organization authorization rules apply. Daemon must persist its
operation before acknowledging and use the stable operation ID when retrying;
ambiguous Runner outcomes are reconciled by GET and must never cause a new ID.

## Persistence and retention

Runner owns SQLite `executions` (ID primary key, request fingerprint, bundle
reference/digest, entrypoint, state, timestamps, exit code, error, stop metadata)
and separate bounded output files on its own volume. Enforce one active
execution transactionally. Lifecycle transitions and corresponding local
outbox events (UUID, sequence, timestamps, execution/operation IDs and versioned
payload) commit together. Output offset denotes bytes already persisted. A
restart reconciles every nonterminal row before accepting work. Retain records,
output and events for this milestone until explicit operator cleanup. At 100 executions the Runner refuses further starts with 503 until an operator removes retained data; output and bundle bytes per execution are bounded. Daemon keeps coordination in its existing
`device_operations`, and any new reconciliation state is persisted before a
Runner call. Hosted operation records and idempotency remain in existing
PostgreSQL tables; there is no new hosted table in 04.

The local integration and non-motion physical Balena smoke have been observed;
device details and the limits of that test are recorded in `docs/evidence/04.md`.
