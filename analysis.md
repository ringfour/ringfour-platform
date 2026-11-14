# Codebase quality assessment

Reviewed 2026-11-10 from the current working tree. This is an early but thoughtfully structured v0.1 platform. Overall quality is good for a milestone-driven prototype: boundaries are clear, contracts and evidence are treated as first-class artifacts, and the API and device protocol have meaningful integration-oriented tests. The main concern is consistency between the implementation, operational docs, and the current uncommitted CLI/daemon rename.

## What is working well

- The repository is organized around independently packaged API, CLI, daemon, Runner, Console, and contracts. Cross-app interfaces are documented rather than coupled through private imports.
- The API uses ASP.NET Core, EF Core migrations, PostgreSQL, explicit auth setup, organization access checks, and Testcontainers-backed tests. That matches the intended architecture.
- Connectivity code takes durability seriously: operations are persisted, sessions are fenced, reconnect replay is explicit, and the daemon keeps deduplication state in SQLite.
- JSON Schemas, representative fixtures, OpenAPI, milestone specifications, evidence, and human-test guides provide unusually good traceability for a young codebase.
- Tests cover useful negative paths such as authorization failures, malformed protocol messages, stale sequences, revocation, idempotency conflicts, and recovery.

## Improvements to prioritize

1. **Finish the executable rename consistently.** The working tree changes the commands to `ringfour-cli` and `ringfour-daemon`, but guides 01–03 and E2E milestone 00 still invoke `ringfour`; development docs and the milestone 03 guide also contain stale names. This makes onboarding and acceptance commands unreliable. Update all references, then verify clean installs and Compose startup use the new entry points.

2. **Make human-testing steps reproducible from any documented working directory.** In `docs/human-testing/03.md`, the interruption section changes directory to the repository root, then runs `uv run ringfour robot status`, which assumes the CLI project context. State explicit working directories or use `uv run --project apps/cli ringfour-cli` consistently. The guide should preserve operation IDs and show their expected values so readers can compare retries directly.

3. **Close the acceptance gap for interruption before acknowledgement.** Milestone 03 explicitly requires interrupting the network before ack and proving one durable effect/result after restart. The evidence describes network loss while idle and an offline queued operation; it does not demonstrate the before-ack timing window. Add a deterministic integration scenario (for example, a controllable test operation/ack boundary) and only claim this gate once observed against real API and daemon processes.

4. **Clarify daemon recovery after credential revocation.** The guide says to rotate and re-enroll, while the daemon enrollment endpoint returns 409 if it already has an identity. Document and implement a supported credential rotation/recovery path, or clearly describe the actual operator procedure. Test recovery after revocation, including persistence across daemon restart.

5. **Strengthen wire-message validation and resource bounds.** The WebSocket endpoint manually parses JSON and validates fields in stages. Keep this code manageable by validating each message type against the contract schema or equivalent typed validators, and enforce a maximum assembled message size. The receive buffer is 64 KiB, but size enforcement across fragmented frames should be explicit and tested.

6. **Reduce version drift in dependencies.** Several Python dependencies use open-ended lower bounds (`>=`) while lockfiles pin tested resolutions. Prefer lockfile-driven deployment and document a deliberate update cadence; where supported, constrain compatibility ranges to avoid unreviewed major-version changes.

7. **Improve operational observability.** Add structured connection/session and operation lifecycle fields to logs, while continuing to exclude credential secrets and tokens. Expose metrics for connected devices, reconnect attempts, operation age/queue depth, and terminal outcomes before the system grows beyond local development.

8. **Make the quality gate easier to trust.** Keep component tests independent per app, and add a single documented CI path that runs contract checks, formatting/type checks, component tests, and milestone E2E gates with explicit prerequisites. Evidence should record exact dependency versions and distinguish local automated checks from Auth0 or hardware gates.

## Current verification limits

The earlier session ran CLI tests (28 passed) and daemon tests (31 passed), and `git diff --check` was clean. The Compose stack was visible as running, but the CLI/daemon rename was still uncommitted and the daemon container showed its prior Uvicorn command. Milestone 03 E2E was not rerun during this review. These checks support the code-quality observations above but do not validate the renamed image or the missing pre-ack interruption case.
