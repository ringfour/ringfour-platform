# App runtime

Apps use independently packaged runtime clients and per-robot Daemon HTTP routes. A persisted private Daemon
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
with the same workspace authorization as run inspection. Immutable App run snapshots are retained; legacy execution tables are retired.

HITL retains pending/submitted/applied semantics. Hosted submission is provisional;
only the Daemon's durable applied acknowledgement confirms it. Bind every prompt,
answer, artifact and Logbook event to its exact run/robot/workspace. App and
installation filters select the same events in global and scoped history.

## Live settings

For a v2 App that declares `runtime_settings`, the Daemon also injects the
run-scoped `RINGFOUR_SETTINGS_URL` and `RINGFOUR_SETTINGS_TOKEN`. Use
`ringfour_runtime.settings.SettingsClient` or `settings.serve` to receive a
complete settings object, validate and apply it in the App, then report
`applied` or a safe `SettingsRejected` reason. Initial settings are acknowledged
before settings-dependent work begins. The result acknowledgement confirms the
App reported its outcome; the hosted installation selection changes only after
the Platform receives that observation.

The helper retries transient failures with the same application ID and keeps a
completed result in process memory so an acknowledgement retry does not call
the handler twice in that process. Delivery is at least once: after an App
process restart, a handler may run again if its result had not reached the
Daemon. Make domain effects idempotent. A rejection leaves prior applied values
and the next-start selection intact. Runtime settings are ordinary revisioned
App data, not secrets.
