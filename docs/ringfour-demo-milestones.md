# Ringfour: milestones from live Logbook to Studio

Prepared 15 November 2026. Working specification for the next demoable slices.

## Goal and order

Let a person start a potentially long-running mission on a robot, follow its events and evidence while it runs, answer a durable request, and open the mission's own interactive interface. After those workflows exist, shape the current internal Console into Studio and freeze the initial feature set.

1. **08 continuation — Live Logbook and artifacts**
2. **09 — Human in the loop**
3. **10 — Mission-defined UI and remote access**
4. **Studio UX sweep, end-to-end acceptance and release freeze**

Keep the present daemon/Runner execution architecture for these slices. Containerized mission packaging is a later migration, not a prerequisite. The mission-facing interfaces below must work without assuming that mission code can see the daemon's host filesystem, so a future container can use them. Do not add scheduling, queues, automatic assignment, a general workflow engine, or a dataset platform to these milestones.

Show progress as soon as there is something real to show. Each milestone ends with a small, repeatable demonstration; calls with vendors and integrators need not wait for the final sweep.

## Current baseline and working-tree caveat

This document is based on a read-only inspection of `/Users/cdr/Work/ringfour` and `/Users/cdr/Work/robocore-sdk` on 15 November. It describes the *visible working tree*, not just committed `72794f6`. The Platform checkout has substantial uncommitted changes, including large-transfer and artifact delivery work; inspect and preserve those changes before implementing. `docs/large-transfer-design.md` and the associated evidence describe work in progress and local integrated results. They do not by themselves establish acceptance of the latest changes on the physical robot.

Already present:

- `apps/daemon/src/ringfour_daemon/runtime_logbook.py` accepts mission-authored entries using an execution-scoped credential. `packages/python-runtime/src/ringfour_runtime/logbook.py` exposes the optional `ringfour_runtime.logbook` client. The SDK remains independent of Platform.
- The daemon SQLite outbox (`logbook.py` and migrations `0009`/`0010`) assigns sequences, retains events, and retries machine-authenticated delivery. `apps/api/src/Ringfour.Api/Logbook/LogbookEndpoints.cs` ingests and provides organization-scoped history. `apps/console/src/components/LogbookPage.tsx` displays a timeline. Agent motion handoff exists separately, but coverage and loss limitations are documented in `docs/logbook-contract.md`; do not infer that every lifecycle/motion event is present.
- `apps/runner/src/ringfour_runner/artifacts.py` snapshots execution files. The current Runner artifact listing requires a terminal execution, and `apps/daemon/src/ringfour_daemon/scripts.py` uploads artifacts only in its terminal branch. Existing API artifact delivery uses an execution-wide completion status; in the visible working tree it still checks a 16-file count. Streaming improvements are in progress, but live per-artifact publishing and cleanup are not established by this inspection.
- `docs/milestones/09-hitl.md` is an existing proposed contract, not a shipped SDK feature. It assigns local deadline authority to the daemon and defers messaging adapters.
- `/Users/cdr/Work/robocore-sdk/robocore-ui` is an optional **Gradio app helper**, not a declarative UI protocol. It supplies map rendering and theme/launch helpers; the SDK does not depend on it. `/Users/cdr/Work/robocore-sdk/playground/base101_teleop/base101_teleop.py` shows a mission-owned loop and Gradio reading shared state. Its old imports/types require a deliberate Ringfour port; do not assume the old package works unchanged. The original README explicitly identifies remote dial-out transport and separate fleet chrome as missing.
- `docs/milestones/10-robotics-e2e.md` currently means Balena/ROS end-to-end acceptance. The requested new 10 is mission UI integration. Preserve that older milestone's hardware acceptance requirements in the release gate or renumber it; do not silently overwrite its meaning or call it already passed.

The implementation team should check the latest source, migrations, local evidence, dependency versions, and any concurrent work before choosing file names or modifying schemas. Proposed interfaces in this document are design requirements, not claims that those endpoints exist today.

## Shared architecture and non-negotiables

```text
Mission process ── optional ringfour_runtime ──► daemon local runtime API
     │                                               │
     ├── SDK ──► Agent ──► ROS / robot               ├── durable SQLite queues
     ├── finalized artifact ──► Runner staging       └── outbound device connection
     └── optional Gradio server                          │
                                                       Platform API + PostgreSQL
                                                       private object storage
                                                              │
                                                     Console / future Studio
```

- The mission owns its work and remains able to run headlessly. A browser disconnect must not stop or restart it. The UI calls mission code; it does not invent robot commands.
- The Agent and SDK remain independently usable. Platform-specific artifact, Logbook and HITL APIs live in `packages/python-runtime`, not as invented Agent RPCs or mandatory core SDK dependencies. A separate optional `ringfour-ui` package may depend on the SDK and Gradio, never the reverse.
- Every execution belongs to a robot and organization. Backend authentication establishes those relationships; never trust a mission-supplied organization or robot ID. Scope events, files, interactions and UI sessions to the execution and authorize each human request.
- Local acceptance and cloud delivery are separate. A locally acknowledged item is not proof of hosted availability. Use durable IDs, transactional writes, replay/deduplication, explicit states, and tests for lost acknowledgements. Do not claim exactly-once transport or complete audit history.
- Continue consuming output and events without blocking physical stop or cleanup on a full history queue. Report gaps and pressure. A hosted stop is an operational request, not an emergency stop or proof of physical stop. Do not change Agent inhibit thresholds or infer verified motion completion.
- Customer data is private to its organization. No automatic third-party sharing, model training, or product analytics based on artifacts or mission content. Define retention and deletion rather than claiming them before implementation.

## Milestone 08 continuation — live Logbook and artifacts

### User-facing outcome

While a mission continues running, it records a meaningful Logbook entry, finalizes a photo, associates that photo with the entry, and an authorized operator sees the event and image in the execution timeline. A disconnected robot keeps pending data for later transfer. The robot removes a staged local copy promptly only after hosted storage and metadata are confirmed.

A Logbook event and an artifact are distinct records: an event says what happened; an artifact is a file. An event can refer to zero or more artifact IDs, and an execution can have files not attached to a specific event. Keep occurrence time, device sequence and Platform receipt time distinct. Mission output and user-supplied descriptions are not proof of physical observations.

### Mission-facing contract

Extend the optional `ringfour_runtime` package with an artifact publisher, alongside the existing `logbook` module. Freeze the actual API and JSON schemas in `packages/contracts` before implementation. Illustrative only:

```python
from ringfour_runtime import artifacts, logbook

photo = artifacts.publish_file("/mission-output/photo.jpg", content_type="image/jpeg")
logbook.info("Inspection point reached", evidence=[photo], location="station-a")
```

A publish call must define **finalization and ownership**: the runtime takes a stable execution-owned immutable snapshot or equivalent before acknowledging locally, so the mission may subsequently modify its original file. Stream large files; no fixed small product cap, but enforce available storage, safe path/regular-file access, configured resource limits and clearly reported failure. A local returned artifact ID means durably queued locally, not remotely stored. Explicitly define what happens when the daemon/runtime or storage is unavailable; no silent success.

The existing Logbook client accepts scalar fields only. Extend the versioned event schema deliberately for artifact references; do not smuggle file bytes into events or assume the illustrative `evidence=` argument exists. Permit an event to arrive before its artifact: show a pending reference, and resolve it once the artifact becomes available. IDs must be bound to the same organization and execution; define behavior if upload never succeeds, the event never arrives, or the mission stops.

### Component responsibilities

- **Runner / mission boundary:** create a per-execution staging area and runtime credentials. Add execution-scoped, safe file finalization usable while running. Reuse the streaming/no-follow work in `artifacts.py` where suitable; keep existing post-exit artifact collection backwards compatible. Persist per-artifact ID, source snapshot, hash, size, MIME type, and local delivery state independently of process state. No global `ready` state may prevent new live artifacts while the execution is running.
- **Daemon:** own a durable local delivery queue/checkpoint and retry worker; reuse the current device identity and streaming transfer work. Record stable artifact ID and immutable content metadata; duplicate publication with identical content is idempotent and conflicting reuse fails. Do not make photo upload wait for terminal status. Bound concurrency and temporary disk usage, avoid starving heartbeats, stop and HITL. Implement explicit acknowledgement back to Runner for safe local snapshot cleanup; handle a lost acknowledgement without deleting the only copy.
- **Platform API / storage:** authenticate the robot, verify execution ownership, stream and verify the uploaded object, and make it visible only after object storage and metadata commit. Persist per-artifact status such as pending/uploading/available/failed; do not conflate artifact availability, end-of-execution collection and execution success. Ensure replay after a lost response does not duplicate bytes or metadata. Address orphaned objects and crash recovery between object store and DB commit. Extend existing `DeviceExecutionEndpoints.cs`, `ScriptRunEndpoints.cs`, entities/migrations and contracts instead of maintaining competing upload paths without a migration plan.
- **Logbook:** link event references to artifact IDs, preserve per-device ordering, existing event IDs and schema versions, and display unresolved/missing/deleted evidence honestly. Existing event history must remain readable. Reuse the existing outbox and ingestion design; its 4,096 pending-event bound and Agent history gaps are existing operational constraints, not evidence of a complete stream.
- **Console:** extend `LogbookPage.tsx` or its current successor with execution-scoped live refresh, artifact state, safe image preview and download. Do not require completion to see events. If using polling for v1, define a practical interval and cleanly stop polling; no new realtime framework required. Authorize file retrieval and handle untrusted content safely—limit inline previews to safe content types and avoid executing uploaded HTML/scripts in the Studio origin.
- **SDK / Agent:** no required changes to core SDK or Agent just to publish a file. Pin and verify versions where a demo reads a camera. Do not claim all Agent motion events are captured or attach unverified actor/stop claims.

### Acceptance

1. A long-running mission emits multiple events and a real camera photo, with the mission still running when the Console displays both. Use a safe stationary or read-only robot scenario.
2. Disconnect Platform, publish locally, restart relevant processes, reconnect; events and artifacts arrive once with correct IDs and no false hosted success.
3. Kill transfer before completion, lose the hosted acknowledgement, and retry. Verify hash, state, safe cleanup and no duplicate record; test object-store success followed by DB failure.
4. Other organizations cannot enumerate, preview, download, or attach the file. Test same filename across executions and ID/content conflicts.
5. Exercise file growth, invalid paths/symlinks, disk exhaustion, queue pressure, large streamed files, and the case where an event precedes its file. Verify stop and mission control remain serviceable.
6. Regress old completed-execution artifact handling and old Logbook event schemas. Record real-process test commands/results and actual hardware evidence separately.

**Demo checkpoint:** show a live execution and photo; no scheduler, general gallery, analytics pipeline, or independent artifacts library is required.

## Milestone 09 — human in the loop

### User-facing outcome

A long-running mission asks a person a question with finite choices. Studio/Console shows it to authorized operators, accepts one answer, displays who answered, and the mission receives the outcome and continues according to its own code. Requests remain meaningful when the browser, device connection, or daemon restarts.

Build on `docs/milestones/09-hitl.md`: local execution-scoped create/get/wait protocol; Platform pending/detail/response APIs; short-lived execution credential; explicit deadline and safe timeout outcome. Add new contracts, not fictional SDK APIs.

### Responsibilities and state

- **Mission runtime:** add an optional `ringfour_runtime` HITL client. Generate or accept a stable request ID for safe retry; expose create and wait/read operations and distinguish answered, timed out, cancelled, pending and delivery error. Waiting must not own the robot's safety policy; mission code decides its safe waiting and timeout behavior.
- **Daemon SQLite:** persist request and outbox transactionally before acknowledging the mission; retain IDs, execution/robot binding, prompt, validated choices, deadline and state. Deliver to Platform on reconnect. Implement cancellation on mission stop and restart reconciliation. A full outbox must yield an explicit failure and never block stop.
- **Platform API/PostgreSQL:** machine-authenticated request replication and organization-scoped human list/detail/respond endpoints. Resolve the actor from the user's access token and membership; operator role is the starting policy. Persist idempotency keys and reject conflicting reuse. Concurrent responders must receive one clear winner and explicit conflict/terminal responses. Store no raw execution credentials.
- **Authority and partition semantics:** the existing milestone assigns the deadline and final outcome to the daemon. Define the full race protocol before coding: a hosted response accepted while offline is **submitted/pending device confirmation**, not a final answer. On reconnection the daemon atomically decides answered vs timeout/cancel using its authoritative deadline, sends the final outcome, and the Platform records and exposes it. The UI must not show the mission resumed merely because the API accepted a response. Define clock assumptions and late delivery; no fabricated approval or automatic unsafe action on timeout.
- **Logbook / Console:** show pending request, choices, submission, confirmed result, actor and time in the execution view. Add durable event production without claiming it captures the robot's physical state. Link evidence only where it already exists. A CLI list/show/respond path follows the existing 09 contract unless consciously deferred with a documented reason.

### Acceptance

1. A mission creates a request, an operator answers, and the mission observes the confirmed answer once. Another organization cannot inspect or respond.
2. Race two operators, retry the same key, reuse it with different content, and verify a single terminal outcome with correct actor attribution.
3. Restart daemon and API while pending; disconnect around response/acknowledgement; race timeout, cancellation and delayed response. Hosted UI must show submitted vs confirmed correctly.
4. Stop the mission with an outstanding request and verify cancellation; check safe behavior when no one answers and when storage or connectivity fails.
5. Exercise the real runtime client, daemon, API, PostgreSQL, Runner and Console/CLI; record reproducible results in `docs/evidence/09.md`. Component mocks are not integrated acceptance.

**Demo checkpoint:** show one person answering one live mission request. No notifications, messaging apps, approval chains, generic form builder, or workflow engine.

## Milestone 10 — mission-defined Gradio UI

### User-facing outcome

A mission author defines an ordinary Gradio app in mission code. An authorized person selects an execution in Console/Studio and opens that mission's UI; the mission keeps running if the viewer closes or loses the browser. UI availability is independent of the mission execution state and does not replace Platform stop/status controls.

### Step A: port and local proof

- Inspect `/Users/cdr/Work/robocore-sdk/robocore-ui` and the Base101 teleop example. Port the optional theme/launch and map helpers as a separate `ringfour-ui` package, updating SDK imports and spatial types only where verified; preserve renderer tests and avoid adding Gradio to core `ringfour-sdk`.
- Pin a supported Gradio version for the demo and test its actual browser requests, asset URLs, streaming/queue protocol and any WebSocket or server-sent-event behavior; do not guess from an older version or assume simple HTTP proxying is enough.
- Define how a mission opts in and announces its local UI endpoint and readiness. Use a runtime-provided execution-scoped registration, or a locked-down Runner-owned endpoint mapping. No arbitrary URL, host, port or user-controlled network target may become a general proxy/SSRF primitive. Bind privately; disable public Gradio sharing by default.
- Mission loop remains independent of browser sessions. UI buttons call mission logic; a closed tab must not change motion state by accident. The mission should continue headlessly.

### Step B: authenticated remote access

- **Daemon:** use an authenticated outbound channel to Platform; avoid an inbound public device port. A dedicated tunnel connection is acceptable if it does not congest the existing command/heartbeat channel. Define connection/session IDs, bounded buffering and concurrency, request and response framing, backpressure, cancellation, timeouts and reconnect behavior. The existing device WebSocket is for commands and cannot be assumed to transport arbitrary browser traffic without a versioned protocol change.
- **Platform:** authenticate the user and organization, authorize access to the *specific running execution*, validate the currently enrolled robot/device session, and route only to the registered mission endpoint. Recheck or expire access on logout, membership/credential revocation, execution stop, UI session expiry and reconnect. Prohibit unauthorized access, arbitrary proxy destinations and forwarding Platform or device secrets to mission code.
- **Browser / Console:** present a mission UI entry point in the execution view, with connecting, offline, unavailable and ended states. Keep independent Platform status/stop controls outside the mission app. Choose an isolated browser origin or another reviewed isolation strategy for mission-authored HTML/JS; a same-origin reverse proxy or iframe alone must not give mission code access to Studio tokens, cookies, privileged APIs or other organizations. Avoid exposing session credentials in URLs, logs or referrers. Validate CSP, cookies, framing and cross-origin behavior against the real Gradio app.
- **Lifecycle and resource limits:** a UI crash or tunnel disconnect marks UI unavailable but does not itself stop or restart a mission. On execution end or replacement, tear down registrations and connections. Large frames or slow viewers must not starve control, stop, Logbook or HITL. A Gradio UI's stop button and hosted stop are not emergency stops.

### Acceptance

1. Existing-style Base101 Gradio app and a headless mission run locally with the Ringfour SDK; demonstrate map/camera presentation only where supported and verified.
2. An authorized operator opens the selected execution's actual Gradio app remotely through the outgoing device connection; test real assets and interactive traffic in a browser, including reconnect and multiple viewers.
3. Other organizations and insufficient roles cannot access or open an existing tunnel; stop, role removal, expiry and credential revocation close or invalidate access. Test arbitrary target/SSRF attempts, origin/cookie isolation and secret leakage.
4. Close the tab, drop network and hang/kill the UI server: mission continues as designed, Platform controls remain visible, and UI availability is accurate. Test bounded resource behavior under slow video consumers.
5. Use a stationary/read-only demonstration first. Any motion test requires separately agreed physical safety and independent stop arrangements; record physical acceptance separately from synthetic tests.

**Demo checkpoint:** open the mission-defined interface from the execution view while the mission runs. A durable 09 request can be displayed in both places, but a Gradio callback alone is not the HITL system of record.

## Studio UX sweep and release gate

Once 08 continuation, 09 and 10 work, review the existing kitchen-sink Console as a single user journey. Make it easy to select a robot and revision, start an execution, open its mission interface, follow live events/evidence, respond to pending requests, stop the mission and return to its history. Keep truthful states for offline, pending, unavailable, failed, incomplete and confirmed. Avoid adding scheduling, queues, workflow designers or generalized fleet management during the sweep.

Run a supervised, repeatable end-to-end acceptance on the exact Base101 configuration: start a safe mission, see a live event and photo, open/close/reopen its mission UI, answer a durable request, inspect history, stop and recover from a routine disconnect. Record deployed commit/image and profile versions, data provenance, sanitised results, and what is simulated. Verify an independent physical means of stopping motion with the robot owner; a hosted stop is not an emergency stop. Preserve the existing `10-robotics-e2e.md` acceptance requirements when renumbering or carrying them forward.

Separate a controlled personal demo from inviting an external customer: before outside access verify organization isolation, mission UI origin isolation and revocation, data access and retention, durable recovery, installation, support ownership, and exact release artifacts. Freeze product features after the workflow is established; fix release blockers and use customer conversations to inform later work.

## Suggested implementation sequence

For each milestone: inspect current working changes → write/version the smallest contracts and migration → implement one vertical slice → component and real-process fault tests → Console view → record evidence → demonstrate. Do not assume every current milestone is complete because a prior document lists it. Preserve ongoing local edits and coordinate changes with their author.

1. **08a:** live mission-authored events and Console refresh; verify existing path with a long-running mission.
2. **08b:** per-artifact finalization, durable delivery, evidence linking and post-ack cleanup.
3. **09:** one durable finite-choice interaction, including offline and timeout race semantics.
4. **10a:** port optional Gradio UI and local execution; **10b:** scoped outbound remote transport and browser isolation.
5. **Studio sweep:** one coherent end-to-end story and supervised acceptance; freeze new features.

Start showing each slice as it works. Containerized missions, self-updating installation, mission scheduling/queues, complete datasets, and additional robot adapters can be planned separately after the first demo unless a concrete pilot requires one sooner.
