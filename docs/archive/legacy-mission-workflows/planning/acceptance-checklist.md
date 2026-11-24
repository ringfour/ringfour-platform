# Milestone acceptance checklist

Active Apps work is tracked separately in [Apps pivot](../../../milestones/apps-pivot.md). AP-01 contract acceptance is complete; existing milestone results below do not establish App-container acceptance.

Status reviewed 2026-11-18. This checklist tracks implementation, automated
verification, and live acceptance separately. Check an item only when its
evidence is recorded in the linked milestone evidence. Use the existing live
Platform and `ringfour-virtual` stacks for live checks; do not create another
stack. The owner performs any required reload.

Evidence: [07](../evidence/07.md), [08](../evidence/08.md), [09](../evidence/09.md),
[10](../evidence/10.md), [multi-user plan](multi-user-acceptance.md).

## Milestone 07 — Development sessions

- [x] Implement development session create, sync, restart, output, stop, and
  cleanup flows.
- [x] Verify the development flow on the existing live Platform and
  `ringfour-virtual` stacks, including local TLS grant use, source generation,
  Runner execution, output, and closure.
- [x] Implement artifact listing/download with manifest and SHA-256 checks.
- [x] Keep `hitl-demo`'s runtime-wheel hash aligned with the wheel present in the
  Runner image (`181bd9f0…`); the old lock hash initially prevented preparation.
- [x] Verify artifact listing/download on the existing live virtual stack;
  verify size/hash, overwrite rejection, and session cleanup.
- [x] Verify offline expiry, workspace recovery, dependency changes, and stale
  generation handling in the documented integrated component environment.
- [x] Implement credential grant expiry/revocation when a device credential is
  rotated or revoked; add daemon coverage and compile the API integration
  project.
- [ ] Exercise credential rotation/revocation end to end in a safe setup. The
  live `virtual-base` has one credential; revoking it could disconnect its
  daemon. Use a disposable robot/credential in the existing stack or another
  safe arrangement before marking this passed.
- [x] Normalize local-grant expiry to PostgreSQL microsecond precision before
  the initial response, preventing the replay response from differing by
  sub-microsecond ticks. API build passes.
- [x] Verify grant idempotent replay on the current live Platform/PostgreSQL:
  two same-key, same-body requests returned byte-identical 202 responses after
  expiry was normalized to persisted precision. The temporary session was
  closed; no separate test stack was created.
- [x] Retire physical-device acceptance as a completion gate per the owner's
  decision. Do not reopen it as an outstanding item.

## Milestone 08 — Logbook

- [x] Implement durable author-entry delivery, hosted history queries,
  pagination/filtering, and replay behavior.
- [x] Implement CLI `logbook list/show` and the Console timeline, including
  selected-choice rendering for interaction events.
- [x] Verify a mission-authored entry on the existing live stack and retrieve
  it through the live Logbook API/CLI with execution and actor attribution.
- [x] Verify live development artifact/Logbook evidence and fix the API
  constraint that rejected evidence-bearing mission entries.
- [x] Add HITL request/answer/timeout/cancellation Logbook events. Request
  attribution uses the execution initiator; confirmed answers use the
  responding operator. A PostgreSQL API integration test confirms the answer
  event is absent until daemon confirmation.
- [ ] Preserve safety-relevant terminal Agent motion events under outbox
  pressure and actual disk exhaustion. The current gap marker can report lost
  history but does not recover lost terminal events.
- [x] Keep mission preparation/start admission behind the Runner free-space
  reserve and surface a clear `insufficient_storage` warning in deployment
  status when Runner rejects the request. A live start was rejected before
  Runner created an execution, and the CLI showed the sanitized warning.
  Active missions are not force-paused on storage pressure. See the 2026-11-17
  live evidence; Console rendering is covered by the deployment status field.
- [ ] Extend Agent event coverage to vendor actions and other uncovered motion
  paths; avoid claiming complete motion history until then.
- [x] Add automatic production mission revision-selected/start/completion/
  failure/stop entries from durable execution state changes, with execution
  actor attribution. Focused unit, Console rendering, and contract checks pass.
- [x] Owner reloaded Platform; verify the new migration and automatic mission
  lifecycle entries through live PostgreSQL and source CLI on a no-motion
  `hitl-demo` execution.
- [x] Confirm the lifecycle entries and HITL choices appear in order in the
  browser Console history for the same live execution as the CLI check.
- [ ] Compare actor presentation for the execution initiator and responding
  operator between CLI and Console; the supplied Console timeline did not show
  actor details.
- [ ] Run 08-MU-01 through 08-MU-04: shared visibility/actor attribution,
  filters/current role, cross-workspace denial, and offline replication/replay.
  The owner deferred credential and multi-user acceptance for later.
- [x] Retire physical-device acceptance as a completion gate per the owner's
  decision.

## Milestone 09 — Human in the loop

- [x] Implement durable request/response state, authorized hosted submissions,
  daemon confirmation, CLI commands, and the Console Overview response queue.
- [x] Make no-deadline waiting the default; support explicit deadline plus safe
  timeout outcome and the SDK `on_timeout(interaction)` callback.
- [x] Add hosted Logbook request and confirmed-answer attribution, including
  response actor and selected label/value.
- [x] Verify contracts (32 tests), Python runtime (5), focused daemon tests
  (15), API interaction/Logbook plus Logbook ingestion tests (4), Console
  tests (27) and build, CLI Logbook tests (2), Ruff checks, and EF model/migration
  consistency. The API integration test uses PostgreSQL Testcontainers.
- [x] Reload both existing stacks. PostgreSQL has applied the interaction and
  Logbook migrations; daemon/Runner health is ready. No new stack was created.
- [x] Live-check an unanswered default request remained pending for 18 seconds;
  after an operator answer, the daemon reported `answered`/`applied` and the
  no-motion mission completed successfully.
- [x] Live-check an explicit 12-second timeout: daemon outcome was `cancel`,
  `on_timeout` printed once, the callback's Logbook entry appeared, and the
  timeout-check mission succeeded.
- [x] Live-check identical request replay and rejection of conflicting payload
  reuse in a running mission.
- [x] Add SDK retry for transient daemon/transport failures and a regression
  test; build the new runtime wheel (`03cf8531…`) and align the `hitl-demo` lock.
- [x] Repeat the live daemon-restart test using the retry-enabled wheel. The
  request stayed pending, the mission stayed running, and after an operator
  answer the daemon reported `applied` and the mission succeeded.
- [x] Live-check cancellation on normal mission stop; the pending interaction
  became `cancelled` with no outcome and the execution became cancelled.
- [ ] Live-check execution binding across two distinct running executions.
  Sequential reuse of a request ID by a second execution was rejected with
  HTTP 409; simultaneous distinct executions remain untestable with the single
  online robot and its one-active-execution constraint.
- [x] Live-check offline response delivery, daemon-confirmed application,
  late-response rejection, and timeout/cancellation winning their races. A
  response stayed submitted during daemon outage; timeout and cancellation
  each won against a provisional response. See live evidence.
- [x] Live-check competing operators, first-winner semantics, idempotent
  retries, and cross-workspace denial. The Developer's response won against a
  concurrent Owner response; the loser got HTTP 409, the winner's retry
  returned the same response ID, and the response actor was the Developer.
  While temporarily a Workspace B member only, the Developer received 404 for
  both Workspace A interaction-list and request-detail access. The original
  Workspace A Developer membership was restored and the temporary Workspace B
  membership removed. See live evidence.
- [ ] Finish the Console queue's UI refresh/retry check. Owner confirmed a
  fresh prompt was visible in Overview, submitted `skip`, and confirmed the
  queue was empty after the run completed. The API showed `answered`/`applied`
  and the queue advanced to the next prompt. In the offline check, refresh left
  the response `pending`/`submitted` until daemon recovery, then
  `answered`/`applied`; the UI's exact waiting label and retry control have not
  been directly confirmed. See live evidence.
- [x] Reload Platform again after the latest API fix that keeps timeout and
  cancellation events unattributed to a human. The prior live history response
  incorrectly fell back to the execution initiator even though the DB stored a
  null actor; live `show` now returns null, and filtering by the initiator omits
  the timeout event.
- [x] Reload `ringfour-virtual` to include the new runtime retry wheel and
  verify restart recovery on the live Runner.
- [x] Verify live Logbook actor filtering for the timeout event through the API
  and source CLI. Browser-level Console verification is still open below.
- [ ] Expand API integration coverage for unauthorized/cross-org access,
  competing responses, retries, timeout/cancellation races, and restart/replay.
- [ ] Add Dashboard component tests for the HITL Overview queue; current Console
  tests cover the Logbook event presentation, not the operator queue.
- [ ] Complete remaining 09-MU-01 and 09-MU-06 cases and record results in
  [multi-user acceptance](multi-user-acceptance.md).
- [x] Defer the optional messaging adapter; it is not required for milestone
  completion.

## Milestone 10 — Mission-defined Gradio UI

Milestone 10 is split into **10a local UI support** and **10b authenticated
remote access**. A mission UI is authored in mission code and remains
independent of the browser session. This is separate from the older
`10-robotics-e2e.md` hardware acceptance; preserve that gate rather than
replacing or silently passing it. Start with a stationary/read-only demo; any
motion testing needs separately agreed safety arrangements.

- [x] Port the optional Gradio theme and map renderer into the separate
  `ringfour-ui` package. Keep Gradio out of `ringfour-sdk` and
  `ringfour-platform-runtime`.
- [x] Verify renderer pixel tests against the current Ringfour SDK spatial
  types and occupancy grid.
- [x] Pin Gradio 6.32.0 for the demo and verify the theme builds with that
  release.
- [x] Inspect browser asset, queue, and streaming requests against a real
  local app in a browser.
- [x] Verify the reloaded Runner image contains the hash-pinned local
  `ringfour-ui` wheel and installs it into a one-off execution environment.
- [x] Prepare and start a published mission revision using the wheel, then
  verify the mission UI in the execution workflow.
- [x] Run a stationary mission UI, close its browser and stop its UI server;
  verify the mission's headless work continues without stopping or restarting.
- [x] Open the stationary local UI demo from the host through the Runner's
  loopback-only port mapping; verify its page and timer callback in a browser.
- [x] Port the old Base101 Gradio example against current Ringfour SDK calls
  and run its stationary/read-only UI locally. Include map or camera views only
  when the virtual robot exposes verified data for them.
- [x] Define execution-scoped UI readiness, authorization and private routing
  boundaries in `docs/mission-ui-transport.md`.
- [ ] Reload the updated Runner and live-check its authenticated
  `/v1/executions/{execution_id}/ui` readiness states against the assigned
  loopback port. Keep the UI target Runner-owned; never accept arbitrary UI
  URLs, hosts or ports. Root-path assignment and proxy routing are still open.
- [x] Live-check Runner's `ready` response for the running Base101 UI,
  `RINGFOUR_UI_PORT=7860` in the mission process, and HTTP 200 on the
  loopback-published local UI. The other readiness states remain open.
- [ ] Add an authenticated outbound UI transport without overloading the
  existing structured command channel. Define framing, authorization,
  buffering, backpressure, cancellation and reconnect behavior.
- [x] Implement the initial separate device UI-status socket and Runner status
  discovery; component build/tests pass. Live Platform handshake and Runner
  `ready` status were checked after reload, but daemon-to-Platform status
  propagation still needs observable evidence. This is status-only; it does
  not proxy Gradio HTTP/SSE yet.
- [x] Add Runner's authenticated per-execution loopback UI proxy with bounded
  request bodies/concurrency, path validation, credential filtering and
  streamed responses. Component tests cover SSE bytes, session-cookie removal,
  authentication and traversal attempts. Live verification awaits the next
  owner-run `ringfour-virtual` reload.
- [ ] Add a Console entry point for the selected execution with connecting,
  offline, unavailable and ended states; retain independent Platform
  status/stop controls.
- [ ] Verify organization/role authorization, revocation, execution-end
  teardown, browser-origin isolation, and no leakage of Platform/device
  credentials to mission code.
- [ ] Verify real Gradio assets and interactive traffic through the remote
  connection, including reconnect and multiple viewers.
- [ ] Close the browser, disconnect transport and terminate the UI server;
  confirm mission continuity and that Platform controls remain usable.
- [x] Keep local and remote evidence in separate sections of
  `docs/evidence/10.md`.
- [x] Add the authenticated per-execution UI-session contract, Operator-gated
  session creation, host-bound single-use bootstrap exchange, cookie hashing,
  and EF migration. Live API check passed: session create 201, bootstrap page
  200, exchange 204, one-use replay 409, and a host-only HttpOnly cookie.
  Console integration and cross-organization/revocation checks remain open.
- [x] Define a separately authenticated device UI tunnel frame contract with
  bounded per-frame headers/chunks, request correlation, cancellation, status,
  and representative SSE fixtures. The status connection uses a different
  socket from the command channel; request/response tunneling remains
  unimplemented and the new socket has not been live-checked.

## Multi-user setup

- [x] Use `tests/users.json` for the first two test accounts; the second account
  was added to the existing workspace as a developer for the competing-role
  and invitation acceptance checks.
- [x] Prepare the competing Developer identity and a separate acceptance
  workspace. The Developer was temporarily added there as Operator for the
  cross-workspace denial check, then removed and restored to Workspace A as
  Developer. Additional mission-author and outsider identities remain useful
  for the full five-user scenario matrix.
- [x] Implement owner-managed email invitations and verified-email acceptance
  in Console/API, with SMTP delivery through MailKit. Live email acceptance
  remains open until SMTP credentials are configured and the reloaded Platform
  sends and accepts a real invitation.
- [ ] Record user aliases, local IDs, roles, workspace IDs, case results, and
  relevant execution/interaction/event IDs without recording credentials or
  tokens.

## Current-stack and documentation closeout

- [ ] Refresh the installed WSL `ringfour-cli`: its `hitl` commands worked, but
  it did not expose `logbook` in this live check. The source CLI was used for
  Logbook verification.
- [x] Reload Platform for the latest actor-null query fix and verify live that
  timeout events remain unattributed and are omitted by the initiator filter;
  exact results are in the milestone evidence.
- [x] Reconcile older evidence passages that are superseded by later
  implementation entries; preserve historical results but make each file's
  current status accurate.
- [ ] Update this checklist as each gate passes. Do not mark a milestone
  complete while its prerequisite or required acceptance cases remain open.
