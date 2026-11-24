# Database schema — Apps pivot

This guide describes the persistence boundaries for the current Apps product
and the identity records retained across the AP-02 reset. It is an orientation
for contributors, not a replacement for EF migrations, model configuration, or
the API contracts. The original mission-era schema guide is preserved at
[database-schema-pre-apps.md](archive/legacy-mission-workflows/contracts/database-schema-pre-apps.md).

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

The platform database still contains legacy mission/Runner tables while their
compatibility callers are being retired. Their presence does not make them a
prerequisite for the Apps workflow; new App persistence belongs to the AP
contracts and migrations below.

## Identity and organization records

AP-02 preserves the existing `users`, `organizations`, and `memberships`
records, including their IDs and Auth0 subject mapping. Membership role is the
source of organization authority. Device and App records are scoped through
organization ownership and robot enrollment; an App manifest cannot assert its
publisher or grant access.

## Apps persistence families

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
  and prevent concurrent legacy or App execution for one robot.
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
4. Keep the PostgreSQL execution slot as the shared legacy/App exclusion point.
   Do not introduce a second independent concurrency lock.
5. Bind evidence and retries to stable owner, robot, operation, installation,
   and run identities. Acknowledgement must follow durable local or hosted
   persistence as required by the relevant contract.
6. Add or update isolated PostgreSQL/SQLite migration and recovery checks with
   the milestone that changes the schema. Do not claim live deployment or
   acceptance from model-level checks alone.

The current EF history includes the Apps persistence, inventory, installation,
run dispatch, and runtime-evidence migrations through
`20261124085103_AppRuntimeEvidence`. AP-08V changes deployment topology only and
adds no schema migration. AP-specific evidence records the exact migration and
runtime acceptance state.
