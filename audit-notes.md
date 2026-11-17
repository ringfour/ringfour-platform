# Ringfour: remove artificial limits and make stop handling responsive

## Objective

Remove small, accumulating limits that interrupt normal Ringfour use; support large mission bundles and artifacts; ensure slow daemon operations cannot block a later stop command; and make the physical safety boundary explicit.

Preserve the existing architecture, organization isolation, durable operation IDs, retry semantics, and conservative handling of uncertain outcomes. Keep the Agent and SDK independently usable without Platform.

Work may span the local repositories:

- `/Users/cdr/Work/ringfour/platform`
- `/Users/cdr/Work/ringfour/ringfour-agent`
- `/Users/cdr/Work/ringfour/ringfour-sdk`
- `/Users/cdr/Work/robots/base101`
- Current examples in `/Users/cdr/Work/ringfour/missions`, where useful for validation.

First inspect the current code and repository instructions; the audit was performed against the November 14, 2026 checkouts and the implementation may have changed.

## 1. Remove accumulating count caps

### Executions

Remove the fixed 100-record limits for Platform script/mission executions, Platform development executions, and Runner’s shared execution table.

Preserve the one-active-execution rule, execution-ID replay and conflict checks, and the ability to reconcile uncertain outcomes. Platform execution history must not be deleted merely to allow new starts.

Inspect what Runner retains per execution. Provide a practical local storage policy that:

- Never deletes files required by an active execution.
- Does not silently delete output or artifacts awaiting synchronization.
- Preserves the state needed for safe reconciliation and the documented retry window.
- Exposes disk pressure and fails clearly when safe operation cannot continue.
- Does not reset execution identity or require deleting the Runner volume.

Avoid turning this into an elaborate retention platform. Make the policy and any user-visible deletion behavior explicit.

### Revisions

Remove the fixed 100-script-revision-per-organization limit. Preserve immutable revision metadata and references used by missions and executions. Keep abandoned-upload cleanup.

Expose or document storage consumption. Do not automatically delete finalized revisions without a defined policy for references and user-visible history.

### Development sessions

Remove the 20-generation-per-session ceiling from both daemon and Runner. Preserve stale-generation detection and operation replay.

Keep the current generation and any generation needed by an active execution. Clean up older snapshots only when they are no longer needed for execution, retries, or recovery.

## 2. Support large mission bundles and artifacts

Remove fixed product-level size caps on mission bundles and artifacts. A mission containing a **2 GB vision model** is a valid use case.

Do not simply remove constants from the existing bounded, in-memory implementation. Update the complete path—CLI, API, object storage, daemon, Runner, and relevant validation—to support large transfers safely:

- Stream uploads, downloads, hashing, archive validation, extraction, and artifact transfers without loading an entire large file or archive into memory.
- Check available storage where practical and return a clear error if the backend or robot cannot accommodate the data.
- Retain digest verification, path validation, safe extraction, and protection against traversal, unsafe links, and accidental or malicious archive expansion.
- Handle interrupted transfers without treating partial data as complete. Resume transfers where feasible; otherwise restart cleanly and clean up partial data.
- Show transfer and preparation progress for large missions.
- Remove or revise request, proxy, server, client, archive, and timeout assumptions that would otherwise prevent the large transfer. Document any deployment infrastructure limits that remain.
- Ensure a mission accepted by one component is not rejected later by a smaller undocumented application limit.

Keep appropriate bounds on control messages and in-memory queues. They must not impose a small fixed limit on mission or artifact payloads.

Report process outcome and artifact collection/upload outcome separately. A process that succeeded but could not save an artifact must not be presented as if the artifact were available.

Separately stored and cached model assets may be designed later; they are not required for this change.

## 3. Keep long-running output useful

Replace the current behavior that retains only the first 64 KiB of each stream.

For the initial implementation, keep a configurable bounded recent window on the robot, defaulting to **10 MiB per stream**. Continue accepting new output after the window fills.

- Use monotonic offsets so clients can detect expired output.
- Clearly indicate gaps or rotation; never present incomplete output as complete.
- Make CLI follow mode and Console continue showing new output after rotation.
- Document the separate hosted-output retention behavior and limits.
- Do not claim complete historical logs unless they are actually stored.

## 4. Keep bounded Logbook queues observable

Retain bounded Agent and daemon queues to protect robot resources.

- Surface backlog, delivery failures, incomplete-history markers, and disk pressure to operators.
- Ensure a full history queue cannot block stop or cleanup.
- Preserve acknowledgement, deduplication, and replay behavior.
- Document what survives an outage and what can be lost under pressure.
- Review replay-record capacity and expiry so legitimate sustained logging fails clearly and recovers when space becomes available.

## 5. Process stop independently of slow operations

Refactor daemon command receipt and execution so a stalled preparation, download, or Runner request cannot prevent it from receiving and beginning to process a later stop.

- Persist commands before acknowledgement and preserve operation-ID deduplication.
- Use bounded scheduling and a priority path for stop and cleanup.
- Preserve necessary ordering and prevent conflicting starts or unsafe concurrent mutations.
- Define stop behavior before, during, and after start or preparation. A superseded start must not execute later.
- Handle reconnect, duplicate delivery, daemon restart, and ambiguous Runner responses without duplicate side effects.
- Surface saturation rather than creating unbounded tasks.

**Timing target:** once a normally functioning daemon has received and validated a stop, it should begin handling it within **one second**, even while an unrelated preparation or Runner request is stalled. Record the test conditions and measured results.

Measure separately:

1. API acceptance to device receipt.
2. Device receipt to daemon stop handling.
3. Daemon handling to Runner acknowledgement.
4. Agent/controller response and any independently verified physical stop.

The one-second target applies only to step 2. It is not an end-to-end or physical-stop guarantee. Document behavior when offline, during restart, under queue saturation, and when Runner is unavailable.

## 6. Define and validate the safety boundary

Documentation and relevant UI must make clear:

- A hosted stop is an operational control, **not an emergency stop**.
- An offline robot may not receive a hosted stop.
- A process exit, accepted stop request, or closed teleoperation session alone does not prove physical stop.
- Agent inhibit and measured-stop claims retain their existing evidence requirements.

For Base101, identify and document the actual independent means of stopping motion and how an operator uses it. Verify it with the owner and physical robot. Do not assume software, a UI button, or a power control is an adequate emergency stop.

Document restart-recovery prerequisites for the deployed hardware profile, including measured-stop policy and controller baseline. Do not automatically clear an inhibit or relax safety thresholds.

Do not command physical motion or change a live robot as part of implementation without a separately agreed, supervised hardware test.

## Acceptance criteria

Automated tests must cover:

1. More than 100 sequential execution records across mission, development, and diagnostic paths without an artificial count rejection.
2. More than 100 finalized revisions in one organization, including reference integrity and abandoned-upload cleanup.
3. More than 20 source syncs in a session, including active-generation and retry behavior.
4. A representative **2 GB mission asset** through the actual transfer and validation path, with measured memory use and tests for insufficient disk, interruption, cleanup, integrity failure, and unsafe archives. If the full-size test cannot run in ordinary CI, provide a repeatable separate test and run smaller boundary tests in CI.
5. Large artifact transfer, interruption, recovery, and separate reporting of process and artifact outcomes.
6. Output beyond the configured recent window, with follow mode continuing and gaps reported.
7. A stalled preparation and stalled Runner request while stop arrives; measure daemon handling against the stated target.
8. Stop racing with start, duplicate delivery, reconnect, restart, and an ambiguous Runner response; verify no duplicate execution or superseded late start.
9. Logbook storage pressure and local disk pressure; verify stop and cleanup remain serviceable and history loss is visible.
10. Organization-isolation, idempotency, and compatibility regressions for affected paths.

Before release, conduct a separately planned, supervised hardware acceptance test against the exact Base101 release configuration. Record image versions, profile, connectivity conditions, observed timings, physical-stop evidence, and the independent stop mechanism. Simulation or component tests do not establish hardware acceptance.

## Delivery

Report:

- Removed limits, retained safeguards, and configuration defaults.
- Migration and storage implications for existing installations.
- Test results, measured memory use for large transfers, and stop-handling timings.
- Known limitations, especially offline behavior and unverified physical safety claims.
- Balena and infrastructure configuration changes.
