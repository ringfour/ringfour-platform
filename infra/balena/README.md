# Balena deployment preparation

This standalone manifest has not itself been deployed. The daemon and Runner
integrated into Odin's existing Balena application passed the non-motion device
smoke recorded in `docs/evidence/04.md`. This manifest contains only the fixed daemon and Runner, separate persistent data
volumes, and a shared staging volume mounted read-only by Runner. It does not
mount the container engine socket or publish the Runner port. The daemon port is
bound to device loopback for enrollment through a local session or SSH tunnel.
The Balena Supervisor owns both containers.

From the repository root, prepare a new standalone build directory:

```bash
python3 infra/balena/prepare.py /tmp/ringfour-balena-build
# Inspect /tmp/ringfour-balena-build/docker-compose.yml before any deployment.
```

The build directory contains independently packaged app source copied from this
checkout. It deliberately has no environment file or credentials. Before a
future deployment, configure the same high-entropy `RUNNER_AUTH_TOKEN` as a
runtime variable for both services and configure the hosted `PLATFORM_API_URL`
for the daemon. Supply credentials through Balena device/service configuration,
not the build directory. Pair the daemon using its displayed code and the
Console, as described in `docs/human-testing/02.md`. A running local Docker stack does not prove Balena networking,
volume ownership, uv preparation, hardware compatibility, or persistence.

The manifest follows [Balena's documented Compose v2.4 field support](https://docs.balena.io/reference/supervisor/docker-compose);
[device/service variables](https://docs.balena.io/learn/manage/variables) are
injected at runtime. Local mode has different variable behavior; consult the
[local mode documentation](https://docs.balena.io/learn/develop/local-mode)
before testing. Actual device version, Supervisor version, commands, results and
cleanup must be recorded in `docs/evidence/04.md` before marking that gate passed.

The Runner build includes the pinned RoboCore C-preview SDK wheel. For a
mission that uses the Agent, set `ROBOCORE_AGENT_URL` on the Runner service to
the Agent's WebSocket URL as reachable **from the Runner container**. The
mission must list the matching wheel and hash in its requirements lock; the
Runner installs it into that mission's separate uv environment. A host-networked
Agent is not at the Runner container's `127.0.0.1`. The existing Agent still
owns the profile and ROS integration.
