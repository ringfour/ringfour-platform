# Ringfour Apps architecture

## System boundary

People authenticate with Auth0 and use the Platform API, CLI and Studio. The API owns workspaces, App catalog/imports, installation
and run intent, authorization, hosted history and evidence metadata. An
enrolled robot Daemon connects outbound to the API using its machine
credential. The Daemon persists local state in SQLite and coordinates the
robot's Agent and App containers.

The Agent owns robot identity/profile, ROS access, capability semantics and
safety. Ringfour does not copy ROS logic or infer physical outcomes from an
App's output. The SDK and Agent remain separate packages with their own release
and compatibility boundaries.

## App release and execution

An App release is an immutable, schema-validated manifest and image digest.
Publishing, sharing and import are organization-scoped. Installing an App
resolves registry authorization, verifies the digest and stores a device
installation without starting a workload. The API sends durable operations to
the enrolled Daemon; fresh device inventory and policy remain authoritative.

Starting creates a server-owned run snapshot bound to its installation,
revision, configuration, image, initiator and robot. Each robot has one shared
execution slot across App runs. Only the enrolled
Daemon manages its robot's App containers. It reconciles the exact run after
restart and does not relaunch an ambiguous start. Accepted commands are not
treated as observed running state.

## Engine authority

The operator provides the Docker Engine Unix socket to the trusted Daemon. That
socket grants Engine-administrator authority; Engine administrators are trusted
by every workload they can manage. Owner labels scope reconciliation and
workload reports, but are not a hostile-Engine security boundary. The API and
App containers never receive the socket. App manifests cannot
choose networks, mounts, privileges, devices, commands or entrypoints.

The Daemon places each App on the robot owner's private network and supplies
only its declared Agent/runtime endpoints and run-scoped credentials. App
containers use a non-root image user, dropped capabilities, no-new-privileges,
resource limits and exact owner/run labels. The Engine socket and Daemon data
volume are never mounted into an App.

## Durable evidence and recovery

The Daemon writes local operation, run, output, interaction and artifact state
before acknowledging or replicating it. App runtime clients use per-run scoped
credentials. Artifacts are immutable local snapshots before acknowledgement;
outputs use independent byte cursors and terminal seals. Human decisions remain
submitted until the device confirms application. Engine/API outages preserve
unknown or pending state rather than fabricating success, stop, cancellation or
approval.

Hosted records use PostgreSQL/EF Core. Device records use additive SQLite
migrations. Preserve both stores and their identity/run bindings through
replacement. See the [database schema](database-schema.md),
[engine operations](apps-engine.md), [hosted runs](apps-runs.md), and
[runtime/evidence contract](apps-runtime.md).

## Authentication and authorization

Auth0 authenticates people. Ringfour maps the Auth0 subject to a local user and
enforces workspace roles in every API route. Device credentials authenticate a
single enrolled robot and are separate from human tokens. Runtime credentials
are scoped to a single App run and are not reused for device or human API calls.
No local-signing-key bypass is allowed in hosted configuration.

## Deployment and migration

Infrastructure owns Daemon and Agent lifecycle. The local server Compose stack
contains API, PostgreSQL and development services; the sibling `ringfour-virtual`
repository owns Base101 and device Compose. Agent and daemon form the default
device stack. The Daemon creates App containers dynamically. An installed App
can run synced source during an [App development session](app-development.md).
