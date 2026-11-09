# Architecture and shared contracts

## Ownership and flows

CLI/Console → hosted API → outbound device connection → daemon → private Runner control API → Python process → existing SDK → existing Agent → ROS/robot.

PostgreSQL owns accounts, organization membership, robots, enrollment, immutable uploaded revisions, requested operations and replicated history. SQLite owns device operation deduplication, execution reconciliation, local events and an upload outbox. Runner persists execution metadata and process output on a dedicated volume. The daemon owns lifecycle coordination; Runner owns environment preparation and OS processes. An API outage does not kill an already running mission, but prevents new hosted requests and human responses until reconnection.

## Platform implementation

The [starter database schema](database-schema.md) defines the first PostgreSQL and SQLite tables, their ownership and transaction boundaries.

Use a C# ASP.NET Core modular monolith on a supported .NET LTS, pinned in milestone 00. Keep identity, organization access, enrollment, deployments, mission coordination, Logbook and HITL as modules within the API application. Use ordinary services, built-in dependency injection and Minimal APIs or controllers. Use PostgreSQL through EF Core and Npgsql, with checked-in EF Core migrations. Avoid microservices, CQRS frameworks, MediatR and event sourcing. OpenAPI and JSON Schema remain the language-independent contracts shared by the CLI, Console and daemon.

Use ASP.NET Core hosted services initially for background processing. Persist critical workflow state, including pending work and retry/reconciliation state as introduced by each milestone, in PostgreSQL. Background services process that durable state; process memory is not a durable queue. Test restart recovery for workflows that rely on background processing.

## HTTP and identifiers

All proposed product endpoints use `/v1`. UUID identifiers, UTC RFC3339 timestamps, explicit schema versions and organization-scoped authorization are required. Errors: `{error:{code,message,request_id,details?}}`; no secrets in messages. Use 400 invalid input, 401 invalid authentication, 403 insufficient membership, 404 inaccessible/missing scoped object, 409 state conflict, 413 size limit, 429 rate limit and 503 unavailable dependency. Never treat request acceptance as execution success.

Mutation endpoints accepting retries use `Idempotency-Key`, scoped to principal, route and target; matching payload replays the same result, different payload returns 409. Persist operation IDs and results. Collections use opaque cursor pagination. Validate schemas on both sides; reject unsupported protocol versions explicitly. Freeze concrete numeric limits and retention defaults in configuration/docs when each feature arrives and test their boundaries.

## Identity and authorization

Configure these Auth0 resources:

| Resource | Configuration |
|---|---|
| Ringfour API | OAuth resource server; audience `https://api.ringfour.eu`; RS256 |
| Ringfour CLI | Native application; OAuth Device Authorization Flow; no embedded client secret |
| Ringfour Web | Single Page Application; Authorization Code + PKCE; used by the internal Console initially and Studio later |

ASP.NET Core JWT Bearer validates access-token signature (RS256), issuer, audience and expiry. Use the Auth0 `sub` claim as external identity, never email as a primary key. Store CLI refresh credentials, when issued, in the OS credential store; redact tokens. Auth0 handles human authentication only. Do not use Auth0 Organizations or model business permissions in Auth0. The daemon and Runner do not use human Auth0 tokens; the Platform API owns robot enrollment and machine credentials, and Runner uses its private daemon authentication.

API owns users, organizations and memberships: owner (membership/device administration), developer (deploy/run/dev), operator (inspect, stop, respond to HITL). Scope every resource lookup by organization. Owners include developer/operator rights; developers include operator rights. Self-service organization creation is allowed for authenticated users; membership mutation is owner-only. Prevent removal of the last owner.

Machine credentials are separate from human tokens, scoped to one enrolled robot; store only hashes where verification permits. Provide revocation, rotation and secret-safe logs. Runner accepts only authenticated private daemon traffic and is never publicly exposed.

## Device protocol

Daemon initiates authenticated TLS WebSocket `/v1/device/connect`; no inbound public device port is required. Envelope: `{version:1,message_id,type,robot_id,sequence,payload}`. Server derives robot authority from credentials rather than trusting the envelope. Commands carry stable `operation_id`, kind, payload and deadline. Persist before acknowledging, replay unacknowledged commands after reconnect, deduplicate by operation ID, and report accepted/running/terminal status. Ordering is per robot; no exactly-once transport claim. Use bounded queues, exponential backoff with jitter, heartbeat interval 15 s and offline threshold 45 s based on server receive time. Resynchronize nonterminal operations after restart.

## Execution and storage

Execution states: queued → preparing → running → succeeded | failed | cancelled; preparation may fail, and cancellation may terminate queued/preparing work. Terminal states cannot reverse. Distinguish desired mission state from observed process state. v0.1 permits one active execution per robot; return conflict for overlap, with no implicit preemption. Run code as a non-root user; bound storage, output and artifact sizes. Stop uses process-group TERM then KILL after configured grace; persist the outcome. On Runner restart, reconcile recorded processes; mark unverifiable lost work failed with reason, never silently replay side effects.

Production revisions contain immutable source, entrypoint, Python constraint, dependency lock and content digest. Verify digest, reject traversal/symlinks escaping the bundle, and prepare a uv environment before activation. Cache downloads across environments; per-revision venv paths are unique. Do not overwrite an active revision. Dev sessions use a mutable workspace and persistent venv; dependency changes explicitly reprovision. Scripts can reuse a compatible cache keyed by lock digest, Python and platform. Retain current/running revisions during garbage collection.

Artifacts use hosted private blob storage behind a small storage interface (filesystem adapter for local tests); metadata and authorization live in PostgreSQL. Upload and download URLs are scoped and short-lived; daemon verifies digests. Set backend, quotas and expiration in milestone 05. No database row may imply bytes exist before upload finalization.

## Durability and observability

Create local durable events from the first execution feature; milestone 08 adds full hosted Logbook queries. Events have event_id, robot_id, local sequence, occurred_at, received_at, actor, correlation/operation/execution IDs, type and schema-versioned payload. Deduplicate replicated events by event_id; use sequence for local ordering, not cross-device wall-clock time. Separate stdout/stderr chunks with offsets from lifecycle events. Bound disk use explicitly; surface backpressure rather than silently losing lifecycle history. Persist intent/outbox atomically with local state.

## Test environments

Platform tests use xUnit; persistence, migrations and database-backed API tests use real PostgreSQL through Testcontainers. Component tests isolate other external boundaries. Local integration uses real service processes and PostgreSQL/SQLite, migrated from empty volumes; test-only Auth0-compatible fixtures cannot substitute for live Auth0 login acceptance. Balena smoke at 04 verifies container/network assumptions; 10 verifies actual SDK/Agent/ROS. Pin and record external versions. Do not publish the internal Console in production deployment manifests.
