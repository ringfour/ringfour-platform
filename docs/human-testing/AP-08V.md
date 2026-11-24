# AP-08V — virtual App deployment acceptance

This guide qualifies the opt-in Runner-free virtual topology. It is separate
from the AP-08/09/10 managed run and runtime checks, and it does not constitute
the AP-13 two-user/two-robot release acceptance. The agent does not reload or
rebuild either live stack; the operator runs the commands below.

## Prerequisites

- AP-07/09/10 API and Daemon consumers are deployed with
  `Apps:InstallationsEnabled=true`; keep existing enrollment and volumes.
- Base101 is running and its external Docker network exists on the same Engine
  that the Daemon will use.
- The host Docker Engine socket is available to the operator. Do not chmod it.
- Review source status before building. `up.sh --apps` refuses dirty Agent, SDK,
  Daemon, and Base101 source by default. Set
  `RINGFOUR_ALLOW_DIRTY_APP_SOURCES=true` only after deliberately reviewing the
  exact local changes; never use this to ship an unreviewed safety change.

## Render configurations

Run from `ringfour-virtual`. Rendering is read-only and does not reload a stack.

```sh
cd /home/cdr/ringfour/ringfour-virtual
export RINGFOUR_ENGINE_SOCKET_HOST_PATH=/var/run/docker.sock
export RINGFOUR_ENGINE_SOCKET_GID="$(stat -Lc '%g' "$RINGFOUR_ENGINE_SOCKET_HOST_PATH")"
export BASE101_NETWORK_NAME=base101_default
export RINGFOUR_APP_NETWORK=base101_default
docker compose -p ringfour-virtual -f compose.apps.yaml config -q

COMPOSE_PROJECT_NAME=ringfour-virtual-2 \
BASE101_NETWORK_NAME=ringfour-sim-2_default \
RINGFOUR_APP_NETWORK=ringfour-sim-2_default \
RINGFOUR_AGENT_NETWORK_ALIAS=ringfour-virtual-2-agent \
RINGFOUR_DAEMON_NETWORK_ALIAS=ringfour-virtual-2-daemon \
RINGFOUR_AGENT_HOST_PORT=10111 \
RINGFOUR_DAEMON_HOST_PORT=8091 \
RINGFOUR_STATUS_HOST_PORT=8093 \
RINGFOUR_ENGINE_SOCKET_HOST_PATH="$RINGFOUR_ENGINE_SOCKET_HOST_PATH" \
RINGFOUR_ENGINE_SOCKET_GID="$RINGFOUR_ENGINE_SOCKET_GID" \
docker compose -p ringfour-virtual-2 -f compose.apps.yaml config -q
```

Review `docker compose ... config` output if any environment interpolation needs
inspection. Confirm both configs contain only Agent and Daemon services, no
Runner, staging volume, Runner UI port or fixed App service. The numbered config
must use the simulator network, Agent/Daemon aliases and unique host ports for
that instance. App containers are created later by their enrolled Daemons.

## Start and inspect one virtual robot

After the API deployment is enabled, the operator starts the opt-in Daemon and
Agent stack:

```sh
./up.sh --apps -d
curl --fail-with-body -sS http://127.0.0.1:8081/health/ready
```

Readiness should report `sqlite: ok` and `engine: ok`. Confirm the existing
robot remains enrolled and its inventory is fresh in the Platform. Inspect the
Daemon and Agent containers:

```sh
docker compose -p ringfour-virtual -f compose.apps.yaml ps
DAEMON="$(docker compose -p ringfour-virtual -f compose.apps.yaml ps -q daemon)"
docker exec "$DAEMON" id
docker inspect "$DAEMON" --format '{{json .Mounts}}'
```

The Daemon must run non-root, have the engine socket as a mount, and retain its
existing `/data` volume. The Agent is on the private Base101 network. Runner,
Runner stage storage, and port 7860 must be absent. Only the Daemon receives the
socket; no App container may receive it.

To replace only the Daemon while preserving the enrollment and pending local
evidence, use the same opt-in launcher and retain its data volume:

```sh
./up.sh --apps -d --force-recreate daemon
```

Verify the robot identity and deployment namespace are unchanged, App inventory
reconnects, and any pending observations are delivered. Do not remove or rename
the project volumes during this check.

## Start two isolated numbered robots

The numbered launcher builds shared images once, then starts the matching
simulator and device Compose projects:

```sh
./upn.sh 2 --apps
```

Inspect instance 1 and 2 independently:

```sh
docker compose -p ringfour-virtual-1 -f compose.apps.yaml ps
docker compose -p ringfour-virtual-2 -f compose.apps.yaml ps
docker network inspect ringfour-sim-1_default
docker network inspect ringfour-sim-2_default
```

Verify distinct network IDs, agent/daemon aliases, ROS domains, daemon data
volumes, robot IDs and deployment namespaces. Instance 2 publishes Agent on
10111, Daemon on 8091 and status on 8093. An App started on one robot must
resolve that instance's Agent and Daemon aliases only. Confirm App containers
join exactly their owner's simulator network and have no socket mount. AP-13
adds authenticated two-user/organization isolation and workload-report checks.

## Stop without deleting retained state

Stop any active App through the Platform and wait for its terminal observation
first. The script refuses active runs or running owned containers. For one
numbered robot:

```sh
./down.sh --apps --instance 2
```

For the singleton, use `./down.sh --apps`. The script obtains the persisted
robot ID and deployment namespace from that Daemon's `/data`, stops its
dispatch, and removes only stopped App containers bearing both exact owner
labels and a run ID. App artifact volumes and Daemon enrollment/outbox state
remain by default. Adding `-v` also deletes per-run artifact volumes only when
their names and ownership labels match the persisted robot and namespace.
Numbered shutdown also stops only its matching `ringfour-sim-N` project and
retains simulator volumes. Do not add `-v` unless state export/recovery is
complete and deletion of that selected project's data is intended.

Record sanitized robot IDs, namespace IDs, network IDs, container IDs, readiness
and recovery outcomes. Do not record access tokens, daemon credential contents,
or full container environments. Any failed or unperformed check remains pending.
