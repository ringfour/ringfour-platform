# Ringfour CLI

The CLI signs in to Ringfour, inspects organizations and robots, publishes
immutable Apps, and operates installations and runs. The Platform authorizes
each request; the enrolled robot Daemon installs and runs Apps through its
configured Engine.

## Install and sign in

Install the latest main build on Linux or macOS:

```sh
curl -fsSL https://raw.githubusercontent.com/ringfour/ringfour-platform/main/cli-installer.sh | sh
ringfour-cli login
```

Release builds include public Auth0 and API defaults. The environment and per-user
configuration can override them.
The installer installs the CLI wheel independently. App builds use the wheel
bundle pinned in `app.json`, which can be installed separately under
`${XDG_DATA_HOME:-$HOME/.local/share}/ringfour/python-artifacts`.

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

## CLI updates

`ringfour-cli version` shows the installed version. `ringfour-cli update-check`
checks the latest Platform release and caches its CLI version. Subsequent CLI
commands can show a cached update notice without contacting GitHub.
`ringfour-cli update` downloads and verifies the release wheel before invoking
`uv tool install --force`. Auth0 credentials, configuration, App files and
SDK wheel cache have their own locations.

## Clone a starter App

With Git installed, run these commands from your projects directory:

```sh
ringfour-cli scaffold
cd my-ringfour-app
python3 main.py
ringfour-cli app validate .
```

Use `ringfour-cli scaffold my-project` to choose the destination. Git preserves
the template history and sets `origin` to the source. The destination must be
new or empty. The command works before sign-in.

The CLI uses the sibling `ringfour-template` repository when run from the
Platform workspace. Standalone installations default to
`https://github.com/ringfour/ringfour-template.git`. To clone an offline copy
or a mirror, pass `--repo /path/to/ringfour-template` or set
`RINGFOUR_TEMPLATE_REPO`.

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
ringfour-cli app show <app-id>
ringfour-cli app import <revision-id>
ringfour-cli app imports
ringfour-cli app install <revision-id> --robot <robot-id>
ringfour-cli app operation <operation-id> --wait
ringfour-cli app installations --robot <robot-id>
ringfour-cli app start <installation-id>
ringfour-cli app settings show <installation-id>
ringfour-cli app settings set <installation-id> --file settings.json
ringfour-cli app logs <run-id> --follow
ringfour-cli app stop <installation-id> --run <run-id>
ringfour-cli app artifacts --app <app-id> --org <workspace-id>
ringfour-cli app artifacts --app <app-id> --robot <robot-id> --path-prefix reports/
```

Publication defaults to unlisted. Use the revision ID from publication for an
import. To discover listed releases, use `app search "Robot Check"`. To start
by App and robot ID, use `app start <app-id> --robot <robot-id>`.

`app configure <installation-id> --config values.json` and
`app update <installation-id> <revision-id> --config values.json` accept JSON
objects validated by the release schema. `app install` and `app update` use
release defaults when `--config` is omitted. Direct shares require
`--share-id`. Install, configure and update return operation IDs; use
`app operation <operation-id> --wait` to follow device acknowledgement. Start returns both a
run ID and operation ID. Stop requires the exact run ID so an old stop request
cannot stop a later run.

To return an idle installation to a previously installed release and
configuration, use `app rollback <installation-id> --revision <revision-id>
--configuration-revision <configuration-revision-id>`. Take both IDs from the
installation history; rollback creates an installation operation and does not
start a run.

Use `app run`, `app artifacts`, `app artifact`, and `app logbook` to inspect
durable run state, output, artifacts, and attributed events. `app interaction`
and `app respond` inspect or submit run-scoped human interactions; submitted
responses remain pending until the device confirms application. `robot
capabilities <robot>` shows observed inventory; add `--revision <id>` for the
server's compatibility assessment.

`app settings show <installation-id>` reports the settings selected for the next start, the last
App-confirmed values, and any unresolved exact-run application. `app settings
set <installation-id> --file settings.json` submits one complete settings object. While the App
is idle, the CLI reports that values were saved for the next start; while it is
running, it returns a pending application ID and run ID. A pending response is
not an applied acknowledgement. The command uses the current selected/applied
revision IDs as compare-and-set preconditions; reload with `show` and review
before retrying a conflict. These values are retained configuration data, not
secret storage.

`app artifacts <run-id>` keeps the existing exact-run listing. Add `--app
<app-id>` to list across runs and installations; optional `--org`, `--robot`,
`--installation`, `--run`, and `--path-prefix` filters narrow the workspace
result. JSON output includes every fetched page and keeps each artifact's run
and robot IDs so duplicate relative paths remain distinct. Download a listed
item with `app artifact <run-id> <artifact-id> --output <path>`; existing
no-overwrite and size/hash verification remain in effect.

The local example uses the Platform's development registry. See
[App authoring](../../docs/apps-authoring.md) for the manifest, pinned template,
local wheel bundle and immutable digest flow, and
[local registry setup](../../docs/local-registry.md) for
registry configuration.

## Device commands

Use `robot list`, `robot show`, `robot status` and `robot capabilities` to
inspect enrolled devices. The authenticated CLI uses the same App APIs and
role checks as Studio.

## App development fastlane

`app init` now creates an editable Dockerfile with shared dependencies/models,
`development` and default `release` targets. Prepare/publish/install the image
once using `app build --development`. In Studio's robot page, select that App
and Enable development mode, then from the source directory run:

```sh
ringfour-cli app dev connect --robot ROBOT_UUID
ringfour-cli app dev sync
ringfour-cli app dev restart --follow
```

Repeat sync/restart for code edits, reusing the image. `app dev status`, `logs`
and `stop` inspect/end that App-scoped session. The LAN source connection is
HTTPS with a Platform-pinned certificate and short-lived token; normal App
permissions/history/logs/files remain in use. This is not the retired mission
`dev` command group or the fixed Runner.

See [the complete development guide](../../docs/app-development.md)
for image/model layout, rebuilding dependencies, session ownership, TTL, virtual
ports and a production release. Initial image transfer still uses the normal
registry; the fast path transfers source snapshots only.
