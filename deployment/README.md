# Platform delivery from main

Every push to main runs the Platform component checks. A passing run builds a
Python 3.13 CLI wheel, amd64 API and Studio images and amd64/arm64 Daemon
images. It tests the wheel entry point, the Daemon startup and the assembled
server stack. GitHub Releases' latest pointer exposes the complete build.
The release tag identifies the run and attempt. Each release contains
checksums, a server bundle with API and Studio digest pins and the Daemon
digest. Publish retries and new runs use distinct artifact versions.

## One-time GitHub and Auth0 setup

Set these GitHub Actions repository variables.

| Variable | Value |
| --- | --- |
| `AUTH0_DOMAIN` | Auth0 tenant hostname |
| `AUTH0_CLI_CLIENT_ID` | Dedicated Native public client ID |
| `AUTH0_WEB_CLIENT_ID` | Studio SPA client ID |
| `AUTH0_AUDIENCE` | API identifier, defaults to `https://api.ringfour.eu` |
| `RINGFOUR_API_URL` | Public HTTPS API base used by the CLI |

Enable Device Code and Refresh Token grants for the CLI and use token endpoint
authentication None. Enable offline access for the API if refresh tokens are
required. Register the public Studio callback, logout and web origins for the
SPA. The build values are public. Runtime passwords and issued user tokens are
credentials. The CLI keeps issued tokens in the OS credential store.

The workflow uses its GitHub token for publication. Set GHCR package
visibility to public for anonymous host pulls, or provision read-only
registry authentication on all pulling hosts. GitHub Releases hosts the
separate CLI wheel and deployment assets.

## CLI

On supported glibc Linux or macOS machines with curl:

```sh
curl -fsSL https://raw.githubusercontent.com/ringfour/ringfour-platform/main/cli-installer.sh | sh
```

On native Windows, run `cli-installer.ps1` from the repository. Both installers
verify the wheel and use uv to provision Python 3.13 and an isolated CLI
environment. Run `ringfour-cli login` after installation. Linux desktop and
headless login needs a working OS keyring backend. The API and Auth0 defaults
can be overridden with the existing CLI configuration or environment.

## N100

Copy `deployment.sh` and `.env.example` to a dedicated directory on the
Linux host. Copy the example to `.env`, restrict its permissions, and set
the persistent PostgreSQL password, Auth0 domain and the HTTPS Studio
origin. The host needs Docker Compose v2, curl, flock, tar and sha256sum.
Run `chmod +x deployment.sh` once if copied without its executable bit.

Run `./deployment.sh` for the latest published build, or
`./deployment.sh main-<run-id>-<attempt>` for a specific one. The script
pulls the matching API and Studio images and applies Compose with the
stable project name `ringfour-server`. It checks API readiness through
Studio. The project keeps PostgreSQL and API storage in named volumes.
When adopting an existing host, confirm the Compose project name and
back up the database and API files before the first run. Review migration
compatibility and restore from backup when required before deploying an
older release.

The ports bind to loopback. Set up the host HTTPS reverse proxy to Studio
and to the API base configured in Actions, with WebSocket upgrades for
the Daemon connection to the API. Configure an App registry reachable by
the API and the robot engines before enabling App installations. The
development registry and Adminer live under `infra/server`.

## Robot Daemon

Keep the robot's existing Compose project, socket permissions, Agent network
and durable data mounts. Set the existing Daemon service's `image` to the
Compose variable `RINGFOUR_DAEMON_IMAGE`. Start with a digest-pinned release.
Create a pin file named by appending `.image` to the robot Compose env file,
containing `RINGFOUR_DAEMON_IMAGE=ghcr.io/...@sha256:...`. Pass both files
to Compose at boot, with the pin file last. Keep that same project name,
Compose file and environment when booting and updating the robot.

Take the updater and systemd units from the same GitHub Release and verify
them against `SHA256SUMS`. Install `robot-update.sh` as an executable under `/usr/local/lib/ringfour`
and the included service and timer under `/etc/systemd/system`. Create
`/etc/ringfour/robot-updater.env` with these values.

| Variable | Value |
| --- | --- |
| `RINGFOUR_ROBOT_COMPOSE_FILE` | Absolute path to the enrolled robot's Compose file |
| `RINGFOUR_ROBOT_ENV` | Absolute path to the Compose environment file |
| `RINGFOUR_ROBOT_PROJECT` | Existing Compose project name |
| `RINGFOUR_DAEMON_READY_URL` | Host-reachable private Daemon `/health/ready` URL |
| `RINGFOUR_ROBOT_MAINTENANCE` | Absolute path to the robot operator's maintenance wrapper |
| `RINGFOUR_DAEMON_SERVICE` | Optional Compose service name, defaults to daemon |

The robot needs Docker Compose v2, curl, jq, flock and sha256sum. The
maintenance wrapper receives the update command as arguments. It must
defer while the robot is busy and hold the site's maintenance gate against
new work for the lifetime of that command. Enable the timer after that gate
is available and after confirming compatibility with the API currently
deployed to the N100. The updater follows the latest complete main release
and updates only the Daemon service. It writes the boot pin after readiness
passes. A failed readiness check leaves the previous pin available. Review
logs and SQLite migration compatibility before restarting the previous image.
