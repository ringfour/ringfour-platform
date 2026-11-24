# Runner

The Runner is a fixed private service on the robot. It accepts authenticated
requests from the daemon, prepares a per-revision `uv` environment, runs code
as a non-root process, and records execution state, stdout, stderr and
artifacts. Its environments isolate dependencies; they are not a security
sandbox. The Platform does not call the Runner directly.

## Get started

Use the sibling [`ringfour-virtual` setup](../../../../../ringfour-virtual/README.md) to
run the Runner with the Agent and daemon, separate from the server stack. For
older milestone acceptance, the integrated test fixture can be started with:

```sh
docker compose -f tests/integration/compose/docker-compose.yml up -d --build
```

The Runner listens privately on port 8082 in Compose and has no published host
port. It needs `RUNNER_AUTH_TOKEN`, a read-only `RUNNER_STAGE_DIR` shared with
the daemon, and persistent `RUNNER_DATA_DIR` storage. Infrastructure owns the
containers; neither service needs the Docker socket. The image includes the
pinned SDK wheel described in [vendor/README.md](runner-vendor-README.md); missions
still install their own locked dependencies into their own environments.

For mission-authored Logbook entries, set `RUNNER_LOGBOOK_URL` to the daemon's
control listener on the robot's private network. Runner derives a credential for
each running execution from the shared private token and passes it to the mission;
it does not pass the master Runner token. The local Compose stack sets this URL.
The mission must separately pin the `ringfour-platform-runtime` integration
wheel; the standalone SDK has no Platform dependency.

Mission-authored Gradio interfaces may separately pin the optional
`ringfour-ui` wheel from `/opt/ringfour/sdk/`. It provides theme and map
helpers; it does not provide remote transport or a Console entry point. The
Runner image receives the wheel from `vendor/` when `ringfour-virtual` builds
the device stack.

Milestone 09 human interactions use the same local listener and independently
derived execution credential through `RINGFOUR_INTERACTION_URL` and
`RINGFOUR_INTERACTION_TOKEN`. The daemon persists the request before returning;
the mission never receives the Runner master credential.

## Develop

```sh
cd apps/runner
uv sync --frozen
uv run ruff check src tests
uv run pytest -q
```

Start with `src/ringfour_runner/main.py`, `executions.py` and `artifacts.py`.
The private protocol is specified in
[`ringfour-runner.v1.yaml`](../../../../packages/contracts/openapi/ringfour-runner.v1.yaml).
For real-process and device testing, see [milestone 04](../human-testing/04.md)
and [milestone 06](../human-testing/06.md).

The private development restart endpoint runs a committed development
generation and reuses its lock-matched environment. Platform, daemon and CLI
integration has been tested locally; see [milestone 07 evidence](../evidence/07.md).


## Storage and execution history

The Runner retains execution records and execution files on its persistent
volume; it does not silently remove output or artifacts, including those that
may not yet have synchronized. Operators must provision and monitor the volume.
New executions are rejected with `insufficient_storage` if available space
would fall below `RUNNER_MIN_FREE_BYTES` (default 268435456 bytes), and
`/health/ready` reports a failed `disk` check under that threshold. Existing
execution IDs and stop requests remain available; clearing the volume would
lose reconciliation history and is not a cleanup procedure.

Development sync keeps the current snapshot, snapshots used by active
executions, and snapshots younger than 24 hours. Older unreferenced snapshot
directories may be removed after a successful sync, while their metadata and
operation IDs remain available for replay. Closing the session removes its
workspace after stopping its execution.
