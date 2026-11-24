# Ringfour CLI

The CLI signs in to Ringfour, inspects organizations and robots, publishes
immutable Apps, and operates installations and runs. The Platform authorizes
each request; the enrolled robot Daemon installs and runs Apps through its
configured Engine.

## Install and sign in

From the Platform checkout, run the CLI directly:

```sh
uv run --project apps/cli ringfour-cli --help
```

For a per-user installation from a shared, readable checkout, install the CLI
into that Unix user's isolated uv tool environment:

```sh
uv tool install --isolated /path/to/ringfour-platform/apps/cli
```

Each Unix user has separate CLI configuration, organization selection and
credential-store state. On WSL/headless Linux, a Secret Service keyring needs a
working D-Bus session and an unlocked keyring before login.

Configure Auth0 and the API address in each user's shell (or that user's
`~/.config/ringfour/config.json`):

```sh
export RINGFOUR_API_URL="http://localhost:8080"
export RINGFOUR_AUTH0_DOMAIN="<your-auth0-domain>"
export RINGFOUR_AUTH0_CLIENT_ID="<your-cli-client-id>"
export RINGFOUR_AUTH0_AUDIENCE="https://api.ringfour.eu"

ringfour-cli login
ringfour-cli whoami
ringfour-cli org list
ringfour-cli robot list
```

Use `ringfour-cli org select <organization-id>` to select a workspace. `logout`
removes the stored login and selected workspace. Auth0 tokens are kept in the
user's credential store, not the config JSON.

## Author and publish an App

Create, publish, discover, import, install and operate an App. Example command
arguments in angle brackets are values you replace with IDs from prior output:

```sh
ringfour-cli app init ./robot-check \
  --app-key robot-check \
  --repository 127.0.0.1:5000/ringfour/robot-check
ringfour-cli app validate ./robot-check
ringfour-cli app build ./robot-check
ringfour-cli app publish ./robot-check
ringfour-cli app search "Robot Check"
ringfour-cli app show <app-id>
ringfour-cli app import <revision-id>
ringfour-cli app imports
ringfour-cli app install <revision-id> --robot <robot-id>
ringfour-cli app operation <operation-id> --wait
ringfour-cli app installations --robot <robot-id>
ringfour-cli app start <installation-id>
ringfour-cli app start <app-id> --robot <robot-id>
ringfour-cli app logs <run-id> --follow
ringfour-cli app stop <installation-id> --run <run-id>
```

`app configure <installation-id> --config values.json` and
`app update <installation-id> <revision-id> --config values.json` accept JSON
objects validated by the release schema. `app install` and `app update` use
release defaults when `--config` is omitted. Direct shares require
`--share-id`. Install, configure and update return operation IDs; use
`app operation --wait` to follow device acknowledgement. Start returns both a
run ID and operation ID. Stop requires the exact run ID so an old stop request
cannot stop a later run.

Use `app run`, `app artifacts`, `app artifact`, and `app logbook` to inspect
durable run state, output, artifacts, and attributed events. `app interaction`
and `app respond` inspect or submit run-scoped human interactions; submitted
responses remain pending until the device confirms application. `robot
capabilities <robot>` shows observed inventory; add `--revision <id>` for the
server's compatibility assessment.

The local example uses the Platform's development registry. See
[App authoring](../../docs/apps-authoring.md) for the manifest, pinned template,
local wheel bundle and immutable digest flow, and
[local registry acceptance](../../docs/human-testing/local-registry.md) for
registry configuration.

## Device commands and migration

Use `robot list`, `robot show`, `robot status` and `robot capabilities` to
inspect enrolled devices. The authenticated CLI uses the same App APIs and
role checks as Studio. Follow the [Apps milestone plan](../../docs/milestones/apps-pivot.md)
and [AP-12 acceptance guide](../../docs/human-testing/AP-12.md) for current
implementation and acceptance status.

Mission push/deploy, script execution and mutable development-session commands
remain in the CLI for legacy callers during migration. Their former walkthrough
is preserved in the [mission-era CLI archive](../../docs/archive/legacy-mission-workflows/applications/cli-legacy-README.md).
