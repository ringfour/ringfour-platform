# Database schema

Platform persistence is defined by the EF migrations, model configuration and API contracts.

## Stores and ownership

- **Platform PostgreSQL** stores users, organizations, memberships, enrolled
  robots, App catalog and deployment state, and hosted evidence. The API owns
  its schema through EF Core migrations. A fresh database is initialized from
the checked-in migration chain.
- **Daemon SQLite** stores one robot's durable enrollment, deployment namespace,
  App operations and runs, private runtime credentials by hash, and ordered
  observations/evidence outbox. The Daemon owns additive SQLite migrations.
- **Object storage** is used for hosted App artifacts where configured. The
  API's relational records bind those objects to their organization, robot,
  installation, run, and immutable metadata.

Keep historical migrations in the chain to support both upgrades and fresh installations.

## Identity and organization records

Preserve `users`, `organizations`, and `memberships` records, including their IDs and Auth0 subject mapping. Membership role is the
source of organization authority. Device and App records are scoped through
organization ownership and robot enrollment; an App manifest cannot assert its
publisher or grant access.

## Apps persistence families

Robot visual identity is optional Console-owned metadata in `robots.appearance`
(nullable PostgreSQL `text`), independent of Agent-owned profiles. Values are
one built-in image/icon key or a bounded raster image data URL (256 KiB decoded).
Only owners can replace it; updates record `appearance_updated` audit events.
See [robot appearance](robot-appearance.md) for the API and migration behavior.

The EF model currently groups records into these related families. Exact
columns, constraints, indexes, and transitions are defined by migrations and
contracts; update those together when changing behavior.

- **Robot inventory:** `robot_app_inventory` is the current projection;
  `app_device_reports` records report identity and sequence for deduplication.
- **Catalog and distribution:** `apps`, `app_revisions`, `app_shares`, and
  `app_imports` record publisher-owned Apps, immutable releases, destination-
  bound sharing, and consumer imports.
- **Installation and configuration:** `app_installations`,
  `app_configuration_revisions`, and `app_operations` record the desired/current
  binding, full immutable configuration snapshots, and idempotent operations.
- **Execution:** `app_runs` and the shared per-robot `robot_execution_slots`
  bind execution to immutable installation/revision/configuration/image state
  and prevent concurrent App execution for one robot.
- **Runtime evidence:** `app_run_outputs`, `app_artifacts`, and
  `app_idempotency_records` with their associated cursors/seals record durable,
  scoped runtime output, artifact delivery, and retry identity.

See [catalog and sharing](apps-api-contract.md),
[installation](apps-installation.md), [runs](apps-runs.md),
[runtime evidence](apps-runtime.md), and [engine authority](apps-engine.md) for
API semantics and authorization boundaries.

## Change and recovery rules

1. Specify persistence with the public contract before implementation.
2. Add PostgreSQL changes through EF Core migrations and keep the model
   snapshot current. Do not edit an already-applied migration to change its
   meaning.
3. Add Daemon SQLite changes as safe, repeatable migrations. Preserve enrollment,
   deployment namespace, pending operation boundaries, and unacknowledged
   observations through replacement.
4. Keep the PostgreSQL execution slot as the per-robot App exclusion point.
   Do not introduce a second independent concurrency lock.
5. Bind evidence and retries to stable owner, robot, operation, installation,
   and run identities. Acknowledgement must follow durable local or hosted
   persistence as required by the relevant contract.
6. Add or update isolated PostgreSQL/SQLite migration and recovery checks with
   each schema change. Do not claim live deployment or
   acceptance from model-level checks alone.

## Organization deletion

`organizations.deleted_at` is nullable; existing rows remain active. Migration `20261127100016_OrganizationDeletion` adds the tombstone. Explicit
owner deletion timestamps this tombstone and removes all memberships in the same
transaction. Robot credentials, pending invitations and shares are revoked;
unused enrollment tokens expire. User rows and other organizations are preserved.
Immutable App provenance, prior runs, artifacts and imports held by other
organizations retain their original foreign keys. This is workspace removal and
access revocation, not a physical purge of audit/provenance records.

Plain self-leave protects the last Owner. A sole member who confirms deletion in Studio uses the explicit deletion endpoint. Connected device sessions disconnect after commit.

Deletion serializes with membership edits and invitation acceptance using the
organization row lock, and with App lifecycle changes using ordered robot row
locks. Installed bindings, active runs/execution slots or nonterminal operations
block deletion with `409 organization_has_apps` until operator cleanup completes.
Deleted publishers are excluded from Store discovery and fresh public revision
reads; existing imported revisions retain their prior access. Device credential
verification also rejects robots in tombstoned organizations, including a
credential issued by an already in-flight request.

## Robot lifecycle and invitation codes

Migration `20261127103524_RobotLifecycle` adds nullable `robots.released_at` and
`robots.deleted_at`, plus audit kinds `robot_unenrolled` / `robot_deleted`.
Release revokes credentials, expires unused enrollment tokens, clears enrolled_at
and removes current negotiated inventory. Active runs/slots and pending
operations block release. Idle installations have installed_binding, candidate
and selected_settings_revision_id cleared and removed_at set transactionally;
revision/configuration and robot/run/provenance rows remain. This owner action
is not recorded as a device-confirmed uninstall.
Deletion also sets deleted_at; active robot lists/access exclude tombstones,
while authorized run/installation history can still resolve their retained robot.
Device auth rejects credentials issued at/before released_at, including stale
in-flight issuances. Enrollment token consumption excludes deleted organizations
and robots and tokens from an older release generation.

Migration `20261127103703_InvitationCodes` makes invitation email/normalized_email
nullable. Both are null for an owner-created shareable code, while email-delivered
invitations retain their existing verified-recipient requirement. Only a SHA-256
code hash is persisted; creation returns the random code once. All invitations
share the existing role, seven-day code expiry, single-account acceptance and
revocation lifecycle. Acceptance locks the organization and invitation and reloads
tracked state after the lock to enforce one claimant under concurrency. Codes
cannot upgrade an already-existing membership. Deleted organizations cannot be
rejoined through either invitation path.

## App development fastlane

Migration `20261127124235_AppDevelopmentFastlane` adds
`app_development_sessions` (robot, installation, human owner, TTL, closure,
source operation/generation/archive digest and short-lived grant hash/expiry)
and `app_development_endpoints` (robot, advertising credential, HTTPS origin,
certificate pin). A partial unique robot index permits one unclosed session;
opening after expiry closes the previous row while holding the robot lock.
All App binding changes and ordinary starts respect active development sessions.
Source bytes stay on the device, not in PostgreSQL. Normal App run snapshots gain
optional development session/generation/digest/expiry provenance.

Daemon SQLite migration 23 adds namespaced local session/grant metadata and
source operation/generation/report checkpoints. ZIPs are stored under the
immutable deployment namespace and session ID in the daemon volume. New
bindings cannot reuse another namespace's source. Existing App runtime, output,
artifact and execution-slot persistence remains authoritative for execution.
