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
GitHub Actions builds the SDK and runtime wheel bundle from the commits pinned
in `packages/python-artifacts/release.json`. For a private SDK repository, set
`RINGFOUR_SDK_READ_TOKEN` to a token with read access to its contents.

## CLI

On supported glibc Linux or macOS machines with curl:

```sh
curl -fsSL https://raw.githubusercontent.com/ringfour/ringfour-platform/main/cli-installer.sh | sh
```

The installer verifies the wheel and uses uv to provision Python 3.13 and an isolated CLI
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

## Managed App registry

Set `RINGFOUR_MANAGED_REGISTRY=1` in the server deployment environment to add
OCI Distribution and its loopback ingress to the existing Compose project.
Set `RINGFOUR_REGISTRY_CERT` and `RINGFOUR_REGISTRY_KEY` to absolute host paths.
Generate a dedicated RSA signing pair, separate from TLS and Auth0:

```sh
umask 077
openssl req -x509 -newkey rsa:3072 -sha256 -noenc -days 365 \
  -subj '/CN=Ringfour registry token signing' -keyout key.pem -out cert.pem
openssl rand -hex 32
```

Use the last output as the persistent `RINGFOUR_REGISTRY_HTTP_SECRET`.
Give the API container's `app` user read access to the signing key. Keep the
private key out of the registry container. Both containers need read access to
the public certificate. Keep the signing files at stable paths across releases.

The registry ingress listens on `127.0.0.1:5000` by default. Point the future
`repository.ringfour.eu` tunnel at that listener. The registry challenge points at
`https://api.ringfour.eu/registry/token` by default. Route that path to the API
and forward Authorization headers without caching the response. If the public
API has a different origin, configure `RINGFOUR_REGISTRY_TOKEN_REALM` accordingly.
No tunnel is installed by deployment. Enable `APPS_INSTALLATIONS_ENABLED` when
robots can reach the registry.

Run `ringfour-cli app push .` as a developer or owner. Ringfour reserves
`apps/<organization UUID>/<app UUID>`, verifies each platform manifest and
records the immutable digests. For managed repositories, existing organization
members and workspace imports can provide pull access; robots get pull tokens
only for authorized preparation operations. Registry deletion is disabled.
Older revisions keep their original digest references and still use their
configured legacy registry.

### Storage and retention

Filesystem storage uses the persistent `app-registry-data` volume. The default
workspace limit is 50 GiB of verified published images, measured once per
repository and digest across all retained revisions. The
`RINGFOUR_REGISTRY_PUBLISHED_QUOTA_BYTES` setting changes the default. The API
configuration key `Apps:ManagedRegistry:OrganizationQuotaBytes:<organization UUID>`
overrides it for an individual workspace. A value of zero disables the
publication quota. This quota applies at revision finalization. The ingress
limits concurrent connections, request rate and individual request size.
Track filesystem capacity separately for blobs such as incomplete uploads.

To move to S3-compatible storage, quiesce registry writes, back up the volume,
and copy the complete storage object tree into the private bucket while
preserving keys. Set `RINGFOUR_REGISTRY_STORAGE=s3` and configure
`RINGFOUR_REGISTRY_S3_REGION`, `RINGFOUR_REGISTRY_S3_ENDPOINT`,
`RINGFOUR_REGISTRY_S3_BUCKET`, `RINGFOUR_REGISTRY_S3_ACCESS_KEY`, and
`RINGFOUR_REGISTRY_S3_SECRET_KEY`. Keep the same public registry host and token
settings. Verify existing digests and a new upload before retiring the volume.

Distribution purges abandoned upload sessions older than seven days. Published
manifests and their upload tags are retained for rollback. For operator-run blob
garbage collection, quiesce writes, back up storage, and run Distribution's
dry run with all manifests retained. Avoid deleting untagged manifests. Check
installed and rollback digests before reopening writes.

Through the tunnel, test a real amd64/arm64 push with a layer representative of
the largest App, then pull both digest references. A direct HTTPS endpoint may
be needed if the tunnel's request limits block standard Docker uploads.
