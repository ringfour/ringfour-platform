# Durable App installation

Installation uses the [owner-scoped engine](apps-engine.md) and the negotiated [device protocol](apps-device-contract.md).
No install-family operation creates a run or invokes container create/start.

API mutations require owner/developer, permanent idempotency and robot row locking.
Import authorization is atomic with installation intent, using the catalog’s exact
publisher/destination share checks. Identity remains unique (robot, App); an
existing installation is addressed explicitly for update, never silently replaced.
Configuration requests are complete replacements: callers may send manifest
defaults explicitly, validators never merge/insert defaults. Configuration rows
are immutable. Failed/staged candidates preserve installed revision/configuration.
Rollback requires a previously applied binding/configuration from that installation.

A mutation commits the operation, complete immutable device command, candidate,
configuration and response before dispatch. New PostgreSQL columns retain command
and first dispatch time; report receipts retain the independent observations
stream. The installation's current binding is its expected generation. Pending
mutation uniqueness and shared robot locking serialize competing edits/apply/start.
Legacy start paths refuse a pending mutation; device application also checks
legacy/App/local-development execution state under SQLite's writer transaction.

API `Apps:InstallationsEnabled` defaults false until API and Daemon consumers are
deployed together. The optional negotiation request field
`supports_app_installations` defaults false; only the enabled Daemon offers
true after obtaining actual engine metadata. An inventory-only session
cannot receive installation commands. Old hello/welcome and negotiation responses
remain unchanged. Reconnect rechecks current-session support, namespace and
credential revocation. Worker dispatch/replay uses the existing v1 command/ack.
Only queued operations without any recorded dispatch can expire server-side;
a possible delivery stays uncertain until device reconciliation supplies an
outcome. Expired device work never begins new preparation/application.

The Daemon fetches the operation-authorized manifest, validates its canonical
hash/configuration/target, obtains a new bounded Agent observation before any
pull, and obtains an ephemeral registry pull grant. Pull/inspect is the only engine
side effect. A staged journal survives replacement and lost acknowledgement.
Before application a SQLite transaction rechecks installed generation, deadline
and robot idleness, then atomically switches revision/image/configuration and
stores the immutable terminal report/outbox. Retry after application resends
that report without pull/reapply. Failure records a failed candidate and keeps
the installed binding unchanged. Runtime/native prerequisites fail closed.

Hosted observations authenticate current session/namespace and bind every ID,
image/configuration and operation to persisted command intent. Batch projection
and receipt insertion are atomic; sequences are strictly contiguous per route.
Identical reports replay original receipt; conflicting IDs, stale generations,
wrong robot/session/namespace or fabricated application are refused. Succeeded
application requires the corresponding exact installed binding and idle robot.
Late terminal reports cannot change an outcome or superseded installed binding.
History/configuration remains readable through authorized installation resources.

The contract pins canonical UTF-8 with an independent emoji/line-separator fixture
across C#, Node, CLI and Daemon. The API retains Unicode scalars instead of the
writer's supplementary-character escapes, and measures configuration byte limits
using that canonical serialization. Existing ASCII fixture/image pins are unchanged.
Executor shutdown waits for in-flight pulls/local mutation transactions before
releasing its writer fence; late application after shutdown is refused. Disk
exhaustion is reported as storage_unavailable without changing the installed binding.
