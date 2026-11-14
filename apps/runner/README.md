# Runner

The Runner is a fixed private service on the robot. It accepts authenticated
requests from the daemon, prepares a per-revision `uv` environment, runs code
as a non-root process, and records execution state, stdout, stderr and
artifacts. Its environments isolate dependencies; they are not a security
sandbox. The Platform does not call the Runner directly.

## Get started

Use [development setup](../../docs/development.md) to configure the local
Compose stack and its shared `RUNNER_AUTH_TOKEN`, then start it from the
repository root:

```sh
docker compose -f infra/compose/docker-compose.yml up -d --build
```

The Runner listens privately on port 8082 in Compose and has no published host
port. It needs `RUNNER_AUTH_TOKEN`, a read-only `RUNNER_STAGE_DIR` shared with
the daemon, and persistent `RUNNER_DATA_DIR` storage. Infrastructure owns the
containers; neither service needs the Docker socket. The image includes the
pinned SDK wheel described in [vendor/README.md](vendor/README.md); missions
still install their own locked dependencies into their own environments.

For mission-authored Logbook entries, set `RUNNER_LOGBOOK_URL` to the daemon's
control listener on the robot's private network. Runner derives a credential for
each running execution from the shared private token and passes it to the mission;
it does not pass the master Runner token. The local Compose stack sets this URL.
The mission must separately pin the `ringfour-platform-runtime` integration
wheel; the standalone SDK has no Platform dependency.

## Develop

```sh
cd apps/runner
uv sync --frozen
uv run ruff check src tests
uv run pytest -q
```

Start with `src/ringfour_runner/main.py`, `executions.py` and `artifacts.py`.
The private protocol is specified in
[`ringfour-runner.v1.yaml`](../../packages/contracts/openapi/ringfour-runner.v1.yaml).
For real-process and device testing, see [milestone 04](../../docs/human-testing/04.md)
and [milestone 06](../../docs/human-testing/06.md).

The private development restart endpoint runs a committed development
generation and reuses its lock-matched environment. Platform, daemon and CLI
integration has been tested locally; see [milestone 07 evidence](../../docs/evidence/07.md).
