# Ringfour Daemon

The Daemon is the enrolled robot's durable coordinator. It maintains its
machine-authenticated connection to the Platform, talks to the Agent over the
robot's private network and persists identity, operations, installations, runs
and evidence in SQLite. Infrastructure owns Daemon/Agent lifecycle.

## App engine

In App mode, set `RINGFOUR_APPS_ENABLED=true`, provide the Engine socket and its
supplementary group, set `RINGFOUR_APP_NETWORK` to the Engine-visible robot
network, and configure the private Agent/runtime URLs. Only this trusted Daemon
receives the operator-provided Engine socket. The Daemon creates App containers
with owner/run labels and bounded resources; Apps never receive the socket or
host data paths. See [engine operations](../../docs/apps-engine.md),
[hosted runs](../../docs/apps-runs.md), [runtime evidence](../../docs/apps-runtime.md).

Keep `/data` and its SQLite state across Daemon replacement. The local control
listener serves runtime evidence on the private robot network; the status page
is read-only.

## Develop

```sh
cd apps/daemon
uv sync --frozen
uv run ruff check src tests
uv run pytest -q
```

From the repository root, the corresponding component suite is
`uv run --project apps/daemon pytest -q apps/daemon/tests`. The [engine integration test](../../tests/integration/daemon-engine.py) exercises the Daemon against a disposable Docker Engine.
