# App engine ownership — AP-08 new design

AP-07 depends on AP-08, as ordered in the pivot backlog. Implement the executor
before enabling installation dispatch. The Docker Engine REST API is pinned to
v1.46 and accessed with the existing locked httpx client over an operator-provided
Unix socket. API reference: https://docs.docker.com/reference/api/engine/version/v1.46/.
No TCP engine, implicit context selection or engine socket in an App is supported.

`RINGFOUR_APPS_ENABLED=true` enables the independent App engine readiness check
and durable writer lock beside the SQLite database. Legacy readiness still checks
Runner when disabled. `RINGFOUR_ENGINE_SOCKET` defaults to /var/run/docker.sock;
`RINGFOUR_APP_NETWORK` and `RINGFOUR_AGENT_URI` are owner settings. The deployment
operator supplies the socket mount and supplementary socket group; never chmod
or restart infrastructure automatically. Engine OS/architecture come from /info,
not the Daemon process. Native facts remain operator verified.

A lifetime nonblocking flock fences the enrolled database/namespace. Every engine
mutation requires that fence and verifies the current persisted enrollment. Run
intent is committed before create; deterministic names and all four ownership
labels bind robot, namespace, installation and run. Inspect verifies exact image
ID and network before start/stop/remove. Conflicting names, foreign labels and
multiple matches fail without mutation. Socket administrators remain trusted.

SQLite migration 0017 records create/start boundaries and stop intent independently
of observed app_runs. A lost create reply can adopt one exactly matching container.
A persisted start intent is never blindly retried: observed running/exited can be
reattached; created/absent after ambiguous start is interrupted. Replacement never
relaunches possibly started work. A missing definitely unstarted container may be
created using the same intent. Terminal output is durably bounded at 64 KiB per
stream and sealed before optional exact-container removal. No image/volume prune.

Owner policy fixes a nonprivileged, non-root workload, root-owned application files,
private PID, dropped capabilities, no-new-privileges, bounded CPU/memory/PIDs/logs,
and one owner network. The immutable image supplies ENTRYPOINT; no command or
entrypoint override, socket, credentials or host bind is accepted. Configuration
is copied as a read-only file into the root-owned application directory before start.
Only the Agent URL/config path and later explicitly bound runtime URLs/tokens may
be passed; AP-08 passes no runtime credentials. Install preparation only pulls
and inspects images, and never invokes create/start. AP-09 owns hosted run dispatch;
AP-10 owns runtime delivery and upstream output/artifact synchronization.
