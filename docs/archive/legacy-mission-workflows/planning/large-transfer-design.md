# Large mission and artifact transfer

This is new Ringfour design for audit item 2. The 2 GiB Garage-backed
CLI-to-Runner transfer and 64 MiB artifact return were exercised end to end;
exact RSS readings and remaining limits are in `docs/evidence/06.md`. Byte
counts use 2^53-1, the exact integer range shared with JavaScript. This does
not establish Balena or physical-device acceptance.

## Mission source

- The CLI builds a deterministic ZIP64 archive in temporary storage using
  bounded chunks, verifies source entries through no-follow file descriptors,
  computes per-file hashes while reading, and computes the archive digest and
  64-bit byte length without loading the archive into memory. Source changes
  during packing must invalidate the build. A retry reopens the same archive
  from offset zero, including after an access-token refresh.
- The API accepts the authenticated upload as a stream into a private temporary
  file, verifies declared length and SHA-256 before publishing it, and removes
  incomplete files on disconnect or error. Finalization validates the archive
  by streaming each regular ZIP member, checks paths, duplicates, links, the
  required entrypoint and UTF-8 lock, and hashes actual extracted bytes. ZIP
  metadata is not trusted. Disk availability, extraction expansion and
  temporary storage must be checked before admitting work; the execution path
  repeats those checks independently on the robot.
- Filesystem and S3-compatible storage expose streaming writes and reads.
  Filesystem publication is atomic; S3 transfers use an appropriately sized
  multipart upload with abort on interruption. A ready revision is not visible
  until a verified object is stored and its database transition commits.
  Restarting a transfer from zero is acceptable; a partially transferred object
  must never be treated as complete. Object-store and ingress infrastructure
  may have external limits, which must be disclosed rather than silently
  replaced by smaller application limits.
- The daemon streams a revision to temporary disk, validates its size and hash,
  validates and extracts members into a temporary staging directory while
  hashing, then atomically publishes the staged directory. The Runner verifies
  source files and makes execution-owned copies in chunks; it never keeps a
  mission asset in a Python bytes object. Both report disk pressure before
  admitting work and preserve the previously active revision on failure.
- Progress describes bytes packed, uploaded, downloaded and prepared; a queued
  or accepted operation is not a claim that transfer or preparation completed.

## Artifacts

- Artifact lists include `artifact_state` and `artifact_error`, independently
  from process state. Existing records are `unknown`; new records begin
  `pending`. The Runner reports `ready` only after publishing a complete
  snapshot, or `failed` with an error if collection fails. The Platform uses
  `uploading`, `ready`, and `failed` for delivery; a failed delivery may be
  retried. A machine-authenticated status endpoint records these transitions.
  Replays are idempotent, and no new artifacts are accepted after `ready`.
- Runner snapshots regular artifacts to an execution-owned temporary directory
  with no-follow descriptor traversal, streaming hash and size accounting,
  adequate free-space checks, and atomic publication. Neither artifact data nor
  a whole artifact list is copied into an unbounded byte array. Incomplete
  snapshots are removed on error; immutable snapshots await acknowledged sync.
- The daemon copies artifact chunks into temporary storage while validating
  offsets, declared size and SHA-256, then streams the verified file to the API.
  The API independently verifies and atomically stores it. Retries must be
  idempotent and never mark a partial object available; downloaded artifact
  responses are streamed. Process completion and artifact collection/delivery
  have separate status so a successful process cannot imply a successful upload.
- Persisted sizes and offsets use signed 64-bit integers throughout. Control
  messages and bounded queues retain explicit independent limits. Existing
  finalized objects and durable operation IDs remain readable across migration.

## Verification gates

Exercise the actual CLI → API → object store → daemon → Runner path with a
representative 2 GiB asset and measure process peak memory. Exercise large
artifact delivery and separate process/artifact outcomes. Include interruption,
retry, disk exhaustion, malformed ZIP, traversal, link, decompression expansion,
digest mismatch, organization isolation and restart cases. Ordinary CI can run
smaller boundary fixtures; the 2 GiB test is a separate repeatable gate. Do not
claim it passed until it has actually run. No robot motion is required.
