# Device-initiated pairing — new Ringfour design

An unregistered daemon creates a cryptographically random 128-bit Base32 pairing
code (26 uppercase characters, displayed in groups of four), a random UUID
request ID and a 256-bit `rfs_` credential. It atomically persists all three
locally in an owner-only file before calling the API. It logs the pairing code
for an operator and serves it on the separate, read-only LAN status page on port 8083; it never logs or displays the credential. The page shows enrollment and observed connection status after pairing and must not expose control API routes. The code is a short-lived secret:
logs containing it must be access-controlled and expire according to operator
log retention. An already enrolled daemon never starts pairing.

`POST /v1/device/pairing-requests` is unauthenticated and accepts request ID,
SHA-256 hex hashes of the normalized code and credential; it returns a 10-minute
expiry. Repeating the same request ID and hashes returns the same request and
expiry; conflicting reuse returns 409. API stores hashes only. The owner calls
`POST /v1/organizations/{org_id}/pairing-claims` with the code and exactly one of
`robot_id` (an existing unenrolled robot in that organization) or `name` (create
one atomically). Only organization owners can claim. Code is single-use, cannot
be claimed after expiry, and concurrent claims have one winner. Errors do not
reveal credentials. The Console presents a single Add robot form for the pairing code and robot
name, with an option to use an existing unenrolled robot. The owner selects the
organization in the Console.

`POST /v1/device/pairing-requests/{id}/poll` accepts the private credential as
proof of possession. Before claim it returns pending; after claim it returns
robot, organization and credential IDs, never the secret. Poll is idempotent so
lost responses and daemon restarts can recover. An expired unclaimed request is
410; the daemon generates a new code and secret. Claimed requests are retained
for recovery until the daemon commits its identity, including across restarts;
the credential is stored before the identity row. Poll returns no data to an
invalid secret. Pairing endpoints use bounded request sizes and per-IP rate
limiting, and deployments must use HTTPS with trusted reverse-proxy settings.
Code creation is additionally capped at 10,000 unexpired requests; expired
unclaimed rows are cleaned on creation. A claimed request links to the robot
and credential and retains only hashes, not raw secrets.

## Persistence

PostgreSQL `device_pairing_requests`: UUID primary key, unique code_hash and
credential_hash, created_at, expires_at, optional claimed_at, robot_id and
credential_id with restrictive foreign keys. Claim and robot/credential/audit
creation are one transaction. Daemon stores its pending code, ID and secret in a
mode 0600 file beside its credential file and uses the existing SQLite identity;
there is no new local database table. The existing token-driven flow stays
available. TLS, rate limiting, log access, restart and recovery are part of the
contract; physical device acceptance must be reported separately.
