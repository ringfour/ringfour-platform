# App runtime — AP-10 new design

App runs use the existing separately packaged runtime clients and per-robot
Daemon HTTP routes. Runner remains available to legacy clients during migration.
App credentials are independent of Runner's secret: a persisted private Daemon
key derives deterministic, run-scoped tokens and SQLite stores only token hashes,
expiry and revocation. Replacement recovers the same credential. Only the owning
robot/run can use it, and new requests require an observed running container.
An engine outage means unknown, never automatic cancellation or approval.

The executor supplies only the Agent/config and bound runtime URL/token variables.
A per-run engine volume stores App artifacts. It is never the Daemon's private
DB/enrollment volume and no host bind is supplied. Publishing snapshots bytes
through the exact owned container archive API into a private durable local spool,
checks relative paths, count/byte limits and hashes, and freezes that publication.
Changing/deleting the App source afterward cannot change the snapshot. Local
spool and upload state survive Daemon replacement. Confirm upload before cleanup.

Output uses the executor's bounded durable prefixes and sealing state. Delivery
accepts completed runs, exact offset retries and truncation indicators; terminal
state must not discard late trusted evidence. Hosted storage is App-run scoped,
with the same workspace authorization as run inspection. Existing legacy records
and immutable App run snapshots are retained.

HITL retains pending/submitted/applied semantics. Hosted submission is provisional;
only the Daemon's durable applied acknowledgement confirms it. Bind every prompt,
answer, artifact and Logbook event to its exact run/robot/workspace. App and
installation filters select the same events in global and scoped history.
