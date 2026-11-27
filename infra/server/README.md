# Server stack

This basic stack runs PostgreSQL, Adminer, the Platform API, and the internal Console.
It intentionally contains no daemon or Runner; those run on the robot. The
Console service uses the app's Vite development server and is for internal
development/basic self-hosting only, not production publishing.

Set `AUTH0_DOMAIN` and `AUTH0_WEB_CLIENT_ID`, then start it with:

```sh
./up.sh
```

The wrapper generates a stable random PostgreSQL password if one is not
already set. If invoking Compose directly, provide `POSTGRES_PASSWORD` yourself.

The API and Console are published on `${API_PORT:-8080}` and
`${CONSOLE_PORT:-5173}`. The browser stays on the address it opened; register
each supported origin in the Auth0 SPA. `CONSOLE_ORIGIN` controls API-generated
email links, not browser redirects. PostgreSQL is reachable only on the
Compose network. For local use, storage defaults to the persistent
filesystem volume. For a hosted deployment, set `STORAGE_BACKEND=s3`,
`STORAGE_S3_ENDPOINT`, `STORAGE_S3_REGION`, and `STORAGE_S3_BUCKET`; provide
object-store credentials through the AWS SDK provider chain and put the API
behind the deployment's HTTPS ingress/reverse proxy. `up.sh` creates and reuses
a random local PostgreSQL password in the ignored `.env.server` file unless
`POSTGRES_PASSWORD` is already set.

## Browse PostgreSQL tables

[Adminer](https://www.adminer.org/) is included as a standalone database browser
using the [official Docker image](https://hub.docker.com/_/adminer), pinned to
`6.3.0`. It connects directly to PostgreSQL on the Compose network. It provides
table/schema inspection, row filtering/sorting and SQL queries, using PostgreSQL
credentials and their database permissions.

The operator starts it with the usual `./up.sh -d`. To add only Adminer when
PostgreSQL is already running, run from the repository root:

```sh
./up.sh -d --no-deps adminer
```

Open **http://localhost:9080** and sign in with:

| Field | Value |
|---|---|
| System | PostgreSQL |
| Server | `postgres` (prefilled) |
| Username | `ringfour` |
| Password | The existing `POSTGRES_PASSWORD`, normally stored in `.env.server` |
| Database | `ringfour` |

The PostgreSQL `ringfour` account can also edit data and schemas; Adminer follows
that account's permissions. Select a table, then **Select data** to browse rows.
Adminer binds only to the host loopback interface. Set `ADMINER_PORT` in `.env`
to change the default port `9080`. This is outside the virtual robots’ allocated
port range and keeps their device-status ports available.

Organization invitations use SMTP through MailKit. Configure `SMTP_HOST`,
`SMTP_PORT` (587 for STARTTLS or 465 for implicit TLS), `SMTP_USERNAME`,
`SMTP_PASSWORD`, `SMTP_FROM_ADDRESS`, and `SMTP_FROM_NAME`. `CONSOLE_ORIGIN`
is used to build the invitation link. Keep SMTP credentials in the ignored
server environment/secret store; do not commit them. The recipient signs in or
creates an Auth0 account, then accepts only when that account has the same
verified email address as the invitation.
See [`docs/organization-management.md`](../../docs/organization-management.md)
for the full invitation flow and acceptance steps.

`tests/integration/compose/docker-compose.yml` is a legacy integration fixture
for older Daemon/Runner tests, not a deployment or current App acceptance
stack. Its historical setup notes are in the
[mission-era archive](../../docs/archive/legacy-mission-workflows/infra/legacy-integration-compose-README.md).


## App image registry (AP-05)

Catalog publication and machine pulls require an explicit registry policy.
Set `APPS_REGISTRY_HOST` and `APPS_REGISTRY_REPOSITORY_PREFIX`; blank values fail
closed. HTTPS is required in the deployed API. Set
`APPS_REGISTRY_ANONYMOUS_PULL=true` only for public pulls. Private registries need
`APPS_REGISTRY_TOKEN_ENDPOINT`, `APPS_REGISTRY_SERVICE`,
`APPS_REGISTRY_USERNAME` and `APPS_REGISTRY_PASSWORD`, supplied through the normal
secret environment. Private policies also require
`APPS_REGISTRY_PUBLISHER_ORGANIZATION_ID` to bind the repository to its publishing
workspace; consumer robots obtain pull authority through imports of that
publisher’s revisions. The configured Distribution Bearer service must issue
pull-only tokens lasting at most five minutes; Basic-only private pulls are not
supported. Additional policies can be supplied through the API configuration's
`Apps:Registries` array in other deployment recipes.

Set `APPS_OFFICIAL_PUBLISHER_ORGANIZATION_ID` to the trusted Ringfour publisher
workspace UUID. Public App Store publication is refused for every other
workspace; ordinary publishers can publish with private (`unlisted`) visibility.
For a registry policy bound to one publisher, use the same UUID for
`APPS_REGISTRY_PUBLISHER_ORGANIZATION_ID`. The value is server configuration
and is never read from an App manifest.

Follow [AP-05 human testing](../../docs/human-testing/AP-05.md) to reload the API
and verify two-workspace publication/sharing/imports. The operator performs that
stack operation. Changing the publisher setting alone needs only a Platform
reload. Deploying AP-18 code also requires both API migrations and the updated
Daemon; see [AP-18 human testing](../../docs/human-testing/AP-18.md).


## Studio serving mode

The Console service now builds the Dockerfile's `production` target. Public Auth0
SPA settings are build arguments; `API_PROXY_TARGET` stays a
runtime setting. The public port is still `${CONSOLE_PORT:-5173}`, mapped to
Nginx port 80. This avoids Vite module delivery and React development effect replay
in ordinary use. The Dockerfile's `development` target is opt-in; `npm run dev`
remains the source-preview workflow. Rebuild Console when its code or public SPA
configuration changes. See [Studio layout](../../docs/studio-layout.md) and
[AP-21 evidence](../../docs/evidence/AP-21.md).
