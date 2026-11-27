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

The API persists the candidate, command and operation before dispatch. Robot row
locking serializes installation changes and starts. An open development session
or occupied execution slot blocks installation changes. A pending installation
change also blocks a start. Before applying a binding, the Daemon checks its
execution slot inside the SQLite write transaction.

Enable installation dispatch with `APPS_INSTALLATIONS_ENABLED=true` on the API
(`Apps:InstallationsEnabled`, default false). The Daemon's `RINGFOUR_APPS_ENABLED`
defaults to true. Configure its Engine socket, private network and Agent URI as
described in [engine operations](apps-engine.md). The Daemon offers installation,
uninstall, run, manifest v2 and live-settings support with its verified engine
facts and deployment namespace. The API also gates live settings with
`APPS_LIVE_SETTINGS_ENABLED`. Dispatch and replay check the current negotiated
session and its credentials.
Only queued operations without any recorded dispatch can expire server-side;
a possible delivery stays uncertain until device reconciliation supplies an
outcome. Expired device work never begins new preparation/application.

For install, update, configure and rollback, the Daemon fetches the operation-authorized manifest, validates its canonical
hash/configuration/target and obtains a fresh Agent observation. Install, update
and rollback obtain an ephemeral registry grant before pulling a new candidate.
Configure and staged retries inspect the existing image locally. A staged
journal survives replacement and lost acknowledgement.
Before application a SQLite transaction rechecks installed generation, deadline
and robot idleness, then atomically switches revision/image/configuration and
stores the immutable terminal report/outbox. Retry after application resends
that report without pull/reapply. Failure records a failed candidate and keeps
the installed binding unchanged. Runtime/native prerequisites fail closed.

Uninstall checks the exact installed revision and configuration, robot idleness and
negotiated uninstall support. It clears the installed binding, retains history
and requires no image pull.

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
