# AP-08V — opt-in virtual Apps topology

## Implementation record · 24 November 2026

Added `ringfour-virtual/compose.apps.yaml` as an opt-in Runner-free device
topology. The existing `compose.yaml` remains the legacy default. The App
Compose file starts Agent and Daemon only, retains Agent and Daemon state
volumes, connects both services to the selected external Base101 network, and
mounts the operator-selected Engine socket only into the non-root Daemon with
the socket's actual supplementary GID. Daemon settings point Apps to the
instance-specific Agent and runtime aliases and report the deployment Engine's
OS/architecture.

`up.sh --apps` validates sibling sources, initialized Base101, the external
simulator network and local Engine socket; it builds only Agent and Daemon and
does not build or stage wheels into the Platform checkout. It refuses dirty
Agent/SDK/Daemon/Base101 source trees unless the operator explicitly sets
`RINGFOUR_ALLOW_DIRTY_APP_SOURCES=true` for a reviewed local preview. The
legacy launcher path retains its Runner wheel staging.

`upn.sh COUNT --apps` assigns each robot its own simulator network, Daemon
namespace, aliases, ROS domain, state volumes and +10 host port block. Runner
aliases and UI ports are omitted. `down.sh --apps [--instance N]` requires the
selected Daemon and its persisted enrollment identity; it rejects active runs,
quiesces Daemon dispatch, rechecks durable run state from a read-only mount of
the exact Daemon data volume, rejects running owner containers, and removes only
stopped run containers matching that robot and namespace. App artifact volumes
are retained by default; explicit `-v` removes only volumes whose names and
robot/namespace labels match the persisted Daemon identity.

Operator instructions are in the [AP-08V guide](../human-testing/AP-08V.md) and
the virtual stack README. The user explicitly directed AP-08V to proceed before
the AP-08/09/10 combined confidence pass; AP-11/AP-12 remain after that pass.

## Checks and gates

- `docker compose -p ringfour-virtual -f compose.apps.yaml config --format json`
  rendered the singleton with only Agent and Daemon, ports 10101/8081/8083,
  and the external `base101_default` network. Assertions confirmed the Engine
  socket is a read-only bind mounted only into Daemon, the configured socket
  GID is passed to Daemon, ports bind to loopback, and no Runner/stage mount is
  present.
- Rendered all 40 numbered configurations using `docker compose -p
  ringfour-virtual-N -f compose.apps.yaml config --format json`. Verified 120
  unique loopback ports, 40 distinct `ringfour-sim-N_default` networks, 80
  distinct Agent/Daemon aliases, 40 distinct Daemon data volumes, and only
  Agent/Daemon services.
- `python3 tests/test_apps_launchers.py -v`: 10 passed. Mocked Docker/Git checks
  cover dirty-source refusal before Docker commands, missing/active Daemon
  refusal, a run appearing during quiescence, owner-scoped stopped-container
  cleanup, default artifact-volume retention, exact-label `-v` cleanup,
  mismatched volume preservation, and Daemon restoration when explicit volume
  cleanup fails.
- `bash -n up.sh upn.sh down.sh` and `git diff --check` passed.
- No Docker image build, actual Engine smoke, Platform reload, or virtual stack
  reload was performed. Socket access/readiness, Daemon replacement, live
  per-instance network/DNS isolation, and state-preserving shutdown remain
  pending operator acceptance. See [human test AP-08V](../human-testing/AP-08V.md).
- The current Base101 submodule has local modifications to
  `profiles/base101.yaml` and `profiles/base101_arm.yaml`. The launcher will
  refuse an App build unless those changes are reviewed and committed, or the
  operator deliberately opts into a local preview. They add measured-stop
  verification thresholds and have not been included in a release pin.

AP-08V implementation checks are complete, but operator acceptance is pending.
No result is claimed for a live virtual App deployment.

**Later sequencing update · 24 November 2026.** The user authorized AP-11 and
AP-12 implementation before the combined AP-08/09/10/08V confidence pass. The
sentence above records the order proposed when this evidence was written; the
operator checks remain pending, while AP-11/AP-12 proceed in parallel with
that pending acceptance gate.
