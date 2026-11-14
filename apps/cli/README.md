# Ringfour CLI

The Ringfour CLI runs on your computer. Use it to sign in, find a robot, publish
and run missions, inspect their output, and iterate on code through a development
session. The Platform authorizes and coordinates work; the robot's daemon and
Runner execute it.

Published missions and development sessions have different data paths:

- **Publish and deploy** creates an immutable revision through the Platform.
  Use it for a mission you want to select and run on a robot.
- **Development sessions** sync a mutable workspace directly to the daemon over
  your local network. The Platform coordinates the session, but source and
  development output travel directly between your computer and the robot.
  A development session does not publish or change the selected mission.

Development sessions are still part of milestone 07 in progress. In particular,
local development artifacts are not yet available. See the
[development-session guide](../../docs/human-testing/07.md) for current test status.

## 1. Get the CLI running

You need [uv](https://docs.astral.sh/uv/), Python 3.13 or newer (uv can install
Python), a reachable Ringfour Platform API configured for Auth0, and a user
with access to an organization. To run missions you also need an enrolled,
online robot with the Ringfour daemon and Runner installed. Pair a new device
through Console; see the [daemon README](../daemon/README.md) and
[development setup](../../docs/development.md) for local infrastructure.

From the Platform repository root, verify the CLI:

```sh
uv run --project apps/cli ringfour-cli --help
```

If `ringfour-cli` is not installed on your PATH, the rest of this tutorial can
be run from any directory by creating a shell alias. Replace the example path
with the absolute path to your Platform checkout:

```sh
alias ringfour-cli='uv run --project /absolute/path/to/platform/apps/cli ringfour-cli'
ringfour-cli --help
```

This alias lasts for the current shell session. Alternatively, install the
local CLI as a uv tool from the Platform repository root:

```sh
uv tool install ./apps/cli
uv tool update-shell
```

Open a new terminal if needed for the PATH change. The tool installation is a
snapshot; reinstall it when you want to pick up later local source changes. If
an installed version already exists, use `uv tool install --force ./apps/cli`.

## 2. Configure and sign in

Set the API URL as reachable **from your computer**, along with the Auth0
settings for your Ringfour CLI application. These are configuration values,
not the robot's machine credential:

```sh
export RINGFOUR_API_URL="http://localhost:8080"
export RINGFOUR_AUTH0_DOMAIN="<your-auth0-domain>"
export RINGFOUR_AUTH0_CLIENT_ID="<your-cli-client-id>"
export RINGFOUR_AUTH0_AUDIENCE="https://api.ringfour.eu"

ringfour-cli login
ringfour-cli whoami
```

Follow the URL and enter the code displayed by `login`; use `login --no-browser`
if the browser is on another machine. Auth0 tokens are stored in your OS
credential store. `ringfour-cli logout` removes the stored login.

Instead of environment variables, you can put `api_url`, `auth0_domain`,
`auth0_client_id` and `auth0_audience` in the JSON configuration file. Its
usual location is `~/.config/ringfour/config.json`; `XDG_CONFIG_HOME` or
`RINGFOUR_CONFIG` can change the path. Environment variables take precedence.
The default API URL is `http://localhost:8080`, and the default audience is
`https://api.ringfour.eu`. See [Auth0 setup](../../docs/development.md#auth0-milestone-01)
for local tenant configuration.

List your organizations and select one:

```sh
ringfour-cli org list
ringfour-cli org select <organization-id>
```

The CLI saves the selection in its non-secret config file. Most commands that
need an organization also accept `--org <organization-id>`. An owner can create
an organization with `ringfour-cli org create "My organization"`.

## 3. Find your robot

```sh
ringfour-cli robot list
ringfour-cli robot show <robot-id>
ringfour-cli robot status <robot-id>
```

`robot status` shows whether the daemon is online and when it was last seen.
Use the robot's ID for commands that explicitly ask for an ID; mission and dev
commands also accept a robot name. Pairing happens in Console and creates the
robot during enrollment. Once paired, it should appear in `robot list`.

For a mission push, `--robot` can be omitted if the project has one remembered
target, `RINGFOUR_ROBOT_ID` is set, or exactly one enrolled robot is online.
`dev start` uses the environment variable or the sole online enrolled robot.
Later context-based commands use their remembered target; specify `--robot` if
more than one matches.

## 4. Create a mission

Make a folder named `hello` with three files. From any parent directory:

```sh
mkdir -p hello
cat > hello/mission.toml <<'TOML'
schema_version = 1
name = "hello"
entrypoint = "main.py"
python_requirement = ">=3.13,<3.14"
lock_file = "requirements.lock"
TOML

cat > hello/main.py <<'PY'
print("Hello from Ringfour!", flush=True)
PY

# This mission has no external dependencies.
: > hello/requirements.lock
```

The Runner's Python must satisfy `python_requirement`. For a mission with
external Python dependencies, list them in `requirements.in` and generate a
hash-pinned lock, for example:

```sh
uv pip compile hello/requirements.in --generate-hashes -o hello/requirements.lock
```

Choose package versions available to the robot's Runner and its architecture.
SDK-backed missions should use the Ringfour SDK version supported by your robot;
see the [mission contract](../../docs/missions-contract.md) for the manifest
and packaging rules. An optional `robot_profile_ref` references an externally
owned robot profile.

Hidden files, `mission.toml` and `ringfour.context.json` are excluded from
published source. Local development sync also excludes `__pycache__`. Paths
must be relative, use ASCII letters, digits, `_`, `-`, `.`, and `/`, and must
not contain symlinks. The bundle limit is 1 MiB total and 256 KiB per file.
Keep secrets out of mission source: excluding hidden files is not secret
scanning.

## 5. Publish and run the mission

From the directory containing `hello`, publish and deploy to your robot:

```sh
ringfour-cli hello push --robot <robot-name-or-id>
```

This prints a mission revision, deployment and operation ID. Preparation happens
asynchronously; `push` alone does not start execution. From the mission folder:

```sh
cd hello
ringfour-cli status             # wait until the revision is selected and ready
ringfour-cli start
ringfour-cli logs --follow      # follow stdout and stderr
ringfour-cli restart            # stop and start with a new execution
ringfour-cli stop
```

The example exits as soon as it prints, so its execution may already be finished
when you inspect it. Interrupting `logs --follow` with Ctrl-C stops the log
follower; it does not stop a running mission. Use `ringfour-cli stop` for that.
A stop requested while a robot is offline is not confirmation that its process
has stopped; check status after it reconnects.

To publish, wait for preparation, start and follow output in one command:

```sh
ringfour-cli . push --robot <robot-name-or-id> --start --follow
```

The equivalent explicit form is `ringfour-cli mission push . --robot <robot-name-or-id> --start --follow`.
If a mission directory has the same name as a CLI command, prefix its path
with `./` when using the folder-first form, for example `ringfour-cli ./mission push`.

To publish and deploy separately, use the IDs printed by the commands:

```sh
ringfour-cli mission publish .
ringfour-cli deploy <mission-revision-id> --robot <robot-id>
ringfour-cli mission status <deployment-id>
ringfour-cli robot operation <robot-id> <operation-id>
ringfour-cli mission start <deployment-id>
ringfour-cli logs <execution-id> --follow
ringfour-cli mission stop <deployment-id>
```

Deployment prepares a candidate and selects it only after preparation succeeds;
it will not replace an active execution. If preparation fails, inspect status
and `last_error`. The previous selected revision remains in place. Explicit
mutation commands accept `--idempotency-key` when you need to retry the same
submission; use a new key when the input changes.

### How project context works

After a push, `ringfour.context.json` in the mission folder remembers the API,
organization, robot, mission, revision and deployment identifiers. Commands run
from the project or its subdirectories use this context and check the live
Platform state; they don't rely on a saved execution ID for logs. The file is
visible, contains no credentials and is excluded from mission uploads. You can
ignore it in version control if it is specific to your machine.

If multiple robots are remembered, add `--robot <name-or-id>` to select one.
Use `--org <organization-id>` when needed. Explicit deployment and execution
IDs remain available as shown above. If the context file is removed, the CLI can
recover an unambiguous matching deployment from the Platform.

## 6. Iterate using a local development session

For this workflow, your computer must reach the daemon's local HTTPS listener;
the Platform must also be reachable for authorization and coordination. The
listener is opt-in on the daemon (`RINGFOUR_LOCAL_DEV_ENABLED=true`, default
port `8084`). The robot needs the current development-session-capable daemon and
Runner. See the [daemon README](../daemon/README.md) and
[milestone 07 guide](../../docs/human-testing/07.md).

From inside the mission folder:

```sh
ringfour-cli dev start --robot <robot-name-or-id> --daemon-url https://<robot-lan-address>:8084
ringfour-cli dev sync
ringfour-cli dev restart --follow
```

Omit `--daemon-url` if the daemon advertises a reachable LAN URL. `dev start`
creates an empty workspace and remembers the session in the project context;
`dev sync` copies the source; `dev restart` runs the synced generation. The
CLI obtains a short-lived grant from the Platform and checks the daemon's
certificate fingerprint before sending the grant to the robot. It does not
connect to Runner directly.

Edit `main.py`, then repeat:

```sh
ringfour-cli dev sync
ringfour-cli dev restart --follow
```

Syncing doesn't automatically start or restart code. Unchanged source is
skipped. A source-only restart reuses a matching prepared environment; changing
the lock requires preparation of a matching environment. Check output and the
session, or close it when finished:

```sh
ringfour-cli dev logs --follow
ringfour-cli dev status
ringfour-cli dev stop
```

Ctrl-C stops following output, not the robot's execution. `dev stop` requests
closure, stops the session's execution and removes its workspace; check
`dev status` for confirmation. Sessions default to one hour; set a lifetime
between 300 and 28800 seconds with `dev start --ttl <seconds>`. Runner stops and
cleans up expired development work even if the Platform is unavailable;
hosted status catches up after reconnection. A session retains up to 20 synced
generations; start a new session after closing it if you reach that limit.
Only one execution can be active on a robot, so stop conflicting production or
development work first.

## 7. Run a standalone script and fetch artifacts

For a one-off Python file, without creating a mission project:

```sh
ringfour-cli run ./hello.py --robot <robot-id>
```

The command uploads the script and follows output by default. Add `--detach`
to return after acceptance, or `--lock requirements.lock` for hash-pinned
external dependencies. It prints an execution ID; use it to inspect, follow or
stop that run:

```sh
ringfour-cli execution show <execution-id>
ringfour-cli logs <execution-id> --follow
ringfour-cli execution stop <execution-id>
```

A script or published mission can write artifacts under the directory specified
by `RINGFOUR_ARTIFACT_DIR`. For example:

```python
import os
from pathlib import Path

Path(os.environ["RINGFOUR_ARTIFACT_DIR"], "result.txt").write_text("hello artifact\n")
```

After the execution completes, use its ID:

```sh
ringfour-cli artifact list <execution-id>
ringfour-cli artifact download <execution-id> result.txt --output ./result.txt
```

Download verifies the size and SHA-256 and will not overwrite an existing
local file. Development-session artifact access is not implemented yet.

## Troubleshooting and command reference

- **No organization selected:** run `org list` and `org select`, or pass `--org`.
- **Robot unavailable:** check `robot status <robot-id>`, the daemon and its
  connection to the Platform. Preparation and starts require an online robot.
- **Execution conflict:** a robot allows one active execution. Stop the current
  mission or development session and check its status before starting another.
- **Local daemon unreachable:** check LAN routing, the HTTPS URL and port, and
  that the local development listener is enabled. An advertised Docker-only
  hostname may not be reachable from your computer; supply `--daemon-url`.
- **Certificate fingerprint mismatch:** verify that the daemon has announced
  its current certificate to the Platform and that the URL points to the intended
  robot. The CLI fails closed; do not bypass the fingerprint check.
- **Source generation pending or stale:** inspect `dev status`, wait for the
  daemon's report to reach the Platform, and retry the sync or restart.
- **Multiple targets:** specify `--robot` and, if needed, `--org`.

Use `ringfour-cli --help` and `ringfour-cli <command> --help` for options. Other
available groups include `robot` for robot status, diagnostic operations and
credential administration, and `daemon` for local identity and legacy
token-based enrollment. The normal pairing flow is managed in Console.

## Develop the CLI

```sh
cd apps/cli
uv sync --frozen
uv run ruff check src tests
uv run pytest -q
```

Start with `src/ringfour_cli/main.py` for commands, `mission_bundle.py` for
packaging, `mission_context.py` for project context, and `dev_sync.py` for local
sync. Component tests use doubles; real-process integration is recorded in
[milestone 06 evidence](../../docs/evidence/06.md) and
[milestone 07 evidence](../../docs/evidence/07.md).
