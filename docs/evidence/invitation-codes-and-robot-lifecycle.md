# Invitation codes and robot lifecycle · 27 November 2026

Implemented the complete API/Console invitation-code flow without an email
service. Owner code creation returns a random single-use code once and persists
only its hash. Existing invitation preview/accept endpoints support these codes
without verified-email lookup; role comes from the stored invitation. Email
invitations keep their existing recipient checks. Invite IDs alone are not grants.
Acceptance now reloads invitation state after locking it so concurrent different
accounts cannot consume one code twice; responses use persisted membership timing
for stable same-account replay. Expiry, revocation, existing-member rejection,
deleted-org protection and owner-only code creation remain enforced. Console
supports copy/select fallback, preview, explicit Join, and workspace refresh.

Added owner-only robot unenroll/delete, with durable releases for old credentials,
revoked access/tokens, session disconnect, current inventory reset, audit records
and robot tombstones. Lifecycle operations are fenced against App negotiation and
blocked by installed Apps, active runs/slots or nonterminal operations. Retained
run/installation history remains accessible to legitimate organization members;
deleted robot IDs cannot be paired again. Credential and token generations from
before release cannot reactivate the binding.

An updated Daemon checks its credential-scoped release receipt at startup before
any executor or background worker starts. Only an explicit, identity-matching
release permits a reset. It holds the existing App writer fence and refuses to
archive while owned containers remain. It snapshots the old SQLite state to an
owner-only archive, closes SQLite handles before atomically installing a fresh
migrated database, removes the old secret/pairing state and starts fresh pairing.
Ordinary credential revocation, expiry, unsupported API, malformed receipt,
connectivity failure or another writer leaves the existing enrollment intact.
The operator restarts the daemon after a confirmed release. No unauthenticated
local reset/control endpoint was introduced.

Migrations: `20261127103524_RobotLifecycle` and
`20261127103703_InvitationCodes`. Public OpenAPI and invitation/receipt schemas
were updated before implementation. Application runtime boundaries stay separate;
process integration communicates over public HTTP endpoints.

Validation evidence:

- API full regression and lifecycle process suite: see
  `/tmp/rf-robot-invites-api-verified.log` (198 cases).
- Actual separate-process integration: disposable Kestrel API + PostgreSQL and an
  independently launched Daemon; public enroll → owner unenroll → daemon restart
  → archived old binding → enrollment into a different organization. Uses signed
  test-only JWTs in isolated Development configuration, no hosted auth bypass.
  Requires `uv sync --locked` in apps/daemon. The test has category ProcessIntegration;
  API-only component runs may use `--filter Category!=ProcessIntegration`.
- Daemon: **124 passed** in `/tmp/rf-robot-invites-daemon-verified.log`; covers fresh
  namespace/archive permissions, unsupported/malformed/wrong-identity receipts,
  normal revocation and writer/container guards, plus existing enrollment/pairing,
  App protocol and engine behavior. Existing dependency deprecation warning only.
- Console: **115 passed** in `/tmp/rf-robot-invites-console-all-final.log`, including
  code creation/join with no org/email, preview failure, owner-only robot controls,
  cancellation, cleanup refusal and reenrollment instructions. TypeScript and Vite
  production builds pass. A configured fixture Auth0 build includes the actual
  feature code (an unconfigured build intentionally shows configuration setup).
- Contracts: **181 passed**; OpenAPI reference checks: **5 passed**. Ruff and
  whitespace checks pass for the changes.

The new App lifecycle fixture initially used changed/sub-microsecond start times
and an end timestamp later than its observation. Corrected the fixture to use the
API's persisted start time and the same terminal observation/end time. The
production run-intent validation was preserved.

No live invitation was created/accepted and no real robot was released/deleted.
No Platform or virtual stack operation was performed. The operator must activate
API/Console and Daemon builds using the commands/actions in the
[human guide](../human-testing/invitation-codes-and-robot-lifecycle.md).

## Follow-up: idle installations during robot removal (2026-11-27)

Corrected the robot release guard: installed-but-idle Apps no longer block
unenrollment/deletion. Under the existing session fence and robot row lock, the
owner action clears installation bindings/candidates/selected settings and sets
removed_at while preserving historical revisions, configurations and runs. Active
runs/slots and pending operations still return robot_has_apps with a specific
blocking message. No synthetic device uninstall acknowledgement is created.
Organization deletion keeps its existing separate installed-App guard.

Validation: `dotnet test apps/api/tests/Ringfour.Api.Tests --no-restore --nologo`
passed **199/199**, including both idle deletion/unenrollment with retained run
history, pending/active blockers and existing Daemon release recovery integration.
`npm run typecheck` in apps/console and `npm run test:openapi` in packages/contracts
passed. Logs: /tmp/rf-api-all.log, /tmp/rf-console-typecheck.log,
/tmp/rf-openapi-refs.log. No live robot was removed and no Platform/virtual stack
operation was performed. Operator activation requires rebuilding/reloading the
Platform API and Console.
