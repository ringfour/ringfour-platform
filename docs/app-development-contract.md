# App development fastlane v1

A development session runs an installed App with source supplied over pinned HTTPS. Models and dependencies live in the image.

An Owner or Developer opens a 300–28800 second session against an installed App
on an idle enrolled robot. One unexpired open session per robot reserves the robot
against ordinary starts and installation changes. The session owner or an Owner
can operate/close it; Operator may inspect but cannot acquire a development grant.
No running App is implicitly stopped on entry. Closing prevents new runs and the
Daemon stops that session's active development run. Independent local expiry also
stops the run while Platform is unavailable. Closure is not device confirmation.

The hosted API uses human Auth0 credentials for these routes.

| Request | Result |
| --- | --- |
| `GET /v1/robots/{robot_id}/app-development` | 200 with the current session or `{"session": null}`. |
| `POST /v1/robots/{robot_id}/app-development` | Open a session for the installation and requested TTL. Supply `Idempotency-Key`. Returns 201. |
| `POST /v1/robots/{robot_id}/app-development/grants` | Rotate the grant using `session_id` and `token_sha256`. Returns 200 with the Daemon URL, certificate pin and grant expiry. |
| `DELETE /v1/robots/{robot_id}/app-development?expected_session_id={session_id}` | Close that exact session. Returns 204. |

The CLI generates a 32-byte bearer secret and sends its SHA-256 hash to Platform.
The grant expires after five minutes or at session expiry, whichever comes first.
Using its device credentials, the Daemon advertises its HTTPS origin and certificate
pin with `PUT /v1/device/app-development/endpoint` and polls
`GET /v1/device/app-development` for the authorized session and grant.
The next successful poll removes revoked grants. Cached grants also expire locally.

The CLI verifies the exact Platform-provided certificate pin and validity before
transmitting its local token. There is no insecure or hosted-source fallback.
`GET /v1/local/app-development/{session_id}/source` returns the generation, archive hash and pending-report status.
`PUT /v1/local/app-development/{session_id}/source` accepts a complete ZIP (16 MiB compressed,
32 MiB extracted, at most 1024 files, 8 MiB per file), a hex digest in `X-Archive-Sha256`,
an integer in `X-Base-Generation` and a UUID in `Idempotency-Key`. No absolute/traversing paths,
symlinks, special files, duplicate files, hidden paths, .env or __pycache__ entries.
Source-only transfers exclude model directories by default; model/image changes
require rebuilding. Snapshots commit atomically, and retries preserve the same
operation ID/body. POST /v1/device/app-development/{id}/source reports only the
operation/base/new generation and archive hash. Restart waits for that report.
Source ZIPs never pass through the hosted API.

`POST /v1/app-installations/{id}/start` accepts an optional `development_session_id`.
It requires the open matching session and its owner/Owner with Developer authority,
then includes server-generated development {session_id,generation,archive_sha256,
expires_at} in the immutable run snapshot. Ordinary App run/operation reporting,
configuration, runtime services, logs, files and history remain in use. Each restart
gets a fresh App run/container with an exact source snapshot; the image is reused.
Images must declare label eu.ringfour.development-source=/workspace, WorkingDir
/workspace and a numeric non-root User. Source is copied into /workspace through
the Engine archive API before process start; no host mount, socket, entrypoint or
command supplied by a manifest. The image defines its executable as usual.
Dependencies/models outside /workspace remain in cached image layers.

Persistence: PostgreSQL app_development_sessions records owner, binding, TTL,
closed state, current source metadata, grant hash/expiry. app_development_endpoints
binds URL/pin to a robot and the advertising credential. SQLite records poll state
and durable source upload operations/ZIPs. Normal App run snapshots retain source
attribution. Lost acknowledgements never authorize a second source generation.

Session replacement guards: grant creation includes the expected session UUID;
DELETE requires expected_session_id in the query. A stale client must never end
or rotate the grant for a newer session. Endpoint advertisements expire after
30 seconds without refresh. A demoted/removed owner is omitted from machine
polls so cached local authority is closed on the next successful poll; existing
grant TTLs still bound disconnected source access. Disabling the Daemon listener
closes its cached sessions and stops development runs instead of leaving them
running without expiry enforcement.

See the [App development guide](app-development.md) for setup and the source sync workflow.
