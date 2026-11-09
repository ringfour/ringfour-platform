# Ringfour v0.1 starter database schema

This is the persistence contract for milestones 00–03. Each milestone ships its own migrations. Milestone 00 proves both databases can migrate and report readiness. Add business tables when their milestone introduces the feature.

PostgreSQL holds platform records through EF Core and Npgsql. SQLite holds daemon records. Runner process metadata remains Runner-owned and is specified in milestone 04.

## Conventions

- Columns are required unless marked optional. Use UUIDs for public identifiers, PostgreSQL `timestamptz` for UTC instants, and one consistent UTC encoding in SQLite. Enable SQLite foreign keys on every connection.
- Enforce relationships with foreign keys and use restrictive deletion for the starter tables. Define deletion and retention when the corresponding workflow is introduced.
- A robot belongs to one organization. Treat that binding as immutable for v0.1. Derive organization ownership through the robot relationship and enforce it when querying. Machine requests derive robot identity from the verified credential.
- Use typed columns for ownership, lifecycle and fields used to filter or enforce invariants. Versioned command and audit payloads can use JSONB in PostgreSQL and validated JSON text in SQLite.
- Store hashes of cryptographically random enrollment tokens and machine credentials. The daemon stores its usable machine credential separately in protected local storage. Keep raw secrets out of audit, idempotency records and logs.

## 00 — Foundation

Check in the EF Core migration baseline and SQLite migration mechanism. Verify migrations against empty stores, repeat them safely, and ensure readiness depends on the expected schema being available. No application table is required at this stage.

## 01 — Identity and membership in PostgreSQL

| Table | Minimum columns | Keys and constraints |
|---|---|---|
| `users` | `id`, `auth0_sub`, `created_at` | PK `id`. Unique `auth0_sub` within the configured single Auth0 issuer. |
| `organizations` | `id`, `name`, `created_at` | PK `id`. Name is nonblank. Display names can repeat. |
| `memberships` | `organization_id`, `user_id`, `role`, `created_at` | Composite PK `(organization_id, user_id)`. FKs to organization and user. Role is `owner`, `developer` or `operator`. Index `(user_id, organization_id)` for organization listing. |

`GET /v1/me` resolves or creates one user per Auth0 subject, including on concurrent first requests. Create an organization and its initial owner in one transaction. Serialize membership changes per organization, for example by locking the organization row before checking owner counts. Concurrent removals and demotions must leave at least one owner.

## 02 — Robot enrollment

| PostgreSQL table | Minimum columns | Keys and constraints |
|---|---|---|
| `robots` | `id`, `organization_id`, `name`, `created_at`, optional `enrolled_at` and `profile_ref` | PK `id`. Organization FK and index `(organization_id, id)`. Profile reference identifies an external profile and may include a version. |
| `enrollment_tokens` | `id`, `robot_id`, `token_hash`, `issued_by_user_id`, `created_at`, `expires_at`, optional `consumed_at` | PK `id`. Unique token hash. Robot and issuer FKs. Expiry follows creation. Index robot ID for scoped lookup and cleanup. |
| `robot_credentials` | `id`, `robot_id`, `secret_hash`, `created_at`, optional `expires_at` and `revoked_at` | PK `id`. Unique secret hash. Robot FK and robot index. Credentials are usable only before expiry and while unrevoked. |
| `robot_audit_events` | `id`, `robot_id`, `occurred_at`, `type`, optional `actor_user_id` | PK `id`. Robot FK and optional user FK. Index `(robot_id, occurred_at, id)`. Capture token issuance, enrollment, rotation and revocation. Device-originated events are attributed to the robot. |

Consume a valid token, create the credential, set `enrolled_at` on first enrollment and write the audit record in one transaction. Use a conditional update or a row lock to prevent double consumption. Rotation creates a new credential and bounds any overlap via the old credential's expiry. Revocation persists before disconnect handling. If the one-time enrollment response is lost, recover through an authorized owner issuing a fresh token and revoking the stranded credential. A consumed token cannot retrieve the original secret.

Daemon SQLite adds singleton `device_identity` with PK `singleton_id` constrained to 1, plus `robot_id`, `organization_id`, `platform_url` and `credential_id`. Commit the binding only after the corresponding secret is available in protected storage. On boot, detect and report a missing or mismatched credential rather than connecting as a different robot.

## 03 — Connectivity and durable operations

| Store and table | Minimum columns | Keys and constraints |
|---|---|---|
| PostgreSQL `robot_presence` | `robot_id`, `session_id`, `last_seen_at`, `daemon_version`, `protocol_version` | Robot ID is PK and FK. An accepted connection replaces the session ID. Only that session may update presence. |
| PostgreSQL `operations` | `id`, `robot_id`, `kind`, `payload_version`, `payload`, `state`, `created_at`, `updated_at`, `deadline_at`, optional `requested_by_user_id`, `acknowledged_at`, `result` | PK `id`. Robot FK and optional user FK. Index `(robot_id, state, created_at, id)` for dispatch and reconciliation. State is `queued`, `accepted`, `running`, `succeeded`, `failed` or `cancelled`. |
| SQLite `device_operations` | `operation_id`, `robot_id`, `kind`, `payload_version`, `payload`, `state`, `deadline_at`, `updated_at`, optional `result` | PK `operation_id`. Robot ID must match the enrolled identity. Index state for startup reconciliation. Same state vocabulary as the hosted operation, with the local record starting at `accepted`. |

Derive online status from an active authenticated connection and the server-side freshness window. Persist last seen for display after restart. Fence heartbeat and status writes from replaced connections. Commit an operation on the platform before dispatch. Commit its local record before acknowledging. Duplicates with identical command content replay existing status. Conflicting reuse of an operation ID is a protocol error. Terminal states are immutable. Use the stored deadline to prevent starting expired work and persist a failure reason when applicable. Retain deduplication records for at least the supported replay window.

For endpoints introduced through these milestones that accept `Idempotency-Key`, persist the principal, route, target, key, request fingerprint, outcome reference and expiry with a unique constraint on principal/route/target/key. Commit the record with the mutation. Handle endpoints that return one-time credentials with their documented recovery path. Never persist a raw credential to replay a response.

## Changes as implementation proceeds

The table names and fields above are a baseline. The agent may adjust physical names, index strategy, internal columns and representations where an implementation detail warrants it. Preserve uniqueness, ownership, concurrency, restart behavior and externally visible contracts. Record the reason and update this document, migrations and affected tests in the same milestone. Changes to a business invariant or a public contract need an explicit decision before implementation.

| Milestones | Persistence to specify before implementation |
|---|---|
| 04–05 | Runner execution and output metadata, daemon reconciliation, hosted script revisions, executions and artifact metadata |
| 06–07 | Mission revisions, deployment selection and desired state, dev session generations and expiry |
| 08–09 | Durable event outbox and Logbook ingestion checkpoints, interaction requests and terminal outcomes |
| 10 | Add storage only if a verified SDK/Agent integration requirement warrants it. Keep the robot profile externally owned. |

Test migrations from empty stores and from the previous milestone. Exercise PostgreSQL constraints against real PostgreSQL, including concurrent first login, last-owner protection, single-use enrollment and operation replay as those features land. Test SQLite with a reopened file-backed database to verify restart behavior.
