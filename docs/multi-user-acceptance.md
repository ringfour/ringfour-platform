# Multi-user acceptance tests

Reusable manual acceptance plan for Ringfour milestones that involve more than
one human account. This first version covers Milestones 08 (Logbook) and 09
(Human in the Loop). Extend the milestone matrix as later multi-user features
ship; keep prior scenarios and evidence rather than replacing them.
Use [Milestone 08's guide](human-testing/08.md) and
[Milestone 09's guide](human-testing/09.md) for their single-user setup and
feature-specific procedures; this document adds cross-user cases.

These are acceptance scenarios, not evidence that either milestone has passed.
Record each result as **Pass**, **Fail**, **Not run**, or **Not supported**, with
the observed result and evidence reference. Do not treat a known missing feature
as a pass.

## 2026-11-17 live run update

Use [Milestone 09 live evidence](evidence/09.md) for execution IDs, interaction
IDs and exact outcomes. The current live run
used the existing Platform and `ringfour-virtual` stacks, `virtual-base`, and
no-motion missions.

| Scenario | Result | Observed |
|---|---|---|
| 09-MU-01 shared pending visibility across workspace members | Partial | Owner and Developer both accessed the same pending request; the Developer submitted the accepted response and Owner inspected the resulting actor and outcome. Simultaneous read-only Console visibility was not separately checked. |
| 09-MU-02 competing operators / first winner | Pass | On request `a981e507-69bd-462f-aead-71abeb90d418`, Owner and Developer submitted different valid choices concurrently. Developer's `battery_station` response `86a98a2c-145c-45b6-9c9c-7a2099722c0d` won; Owner received 409. Retrying the Developer's same choice/key returned the same response ID. |
| 09-MU-03 Console queue | Partial | Offline submission remained pending/submitted after refresh until daemon confirmation. In a fresh run, Owner saw the pending prompt in Overview, submitted `skip`, and confirmed Overview was empty after the run completed; API showed `answered`/`applied` and the queue advanced to the next prompt. Exact waiting label and Console retry behavior remain unverified. |
| 09-MU-04 timeout, late response and cancellation races | Pass | Offline submitted response became `rejected_timed_out`; a separate provisional answer after local cancellation became `rejected_cancelled`. |
| 09-MU-05 cross-workspace denial | Pass | The Developer was temporarily moved to Workspace B as Operator and removed from A. Listing A's interactions and showing an A request both returned 404. The original Developer membership in A was restored and B membership removed. |
| 09-MU-06 responder vs. Logbook attribution | Partial | Owner inspection of the interaction showed the Developer as response actor. The corresponding Logbook event was not compared in this run. |
| Console retry control | Not run | Backend idempotent retry was verified with the CLI. The Console's retry control or duplicate-submit behavior was not directly exercised. |
| Execution binding across two simultaneous executions | Not supported by current test topology | One online robot and one-active-execution-per-robot constraint; sequential cross-execution request-ID reuse was rejected with HTTP 409. |

## Test environment and safety

- Use the existing live Platform and `ringfour-virtual` stacks. Do not create a
  second stack or database for this acceptance run.
- Use `virtual-base` and the no-motion `hitl-demo` mission. It waits for human
  choices and writes Logbook entries; it does not command robot motion.
- Physical-device acceptance has been retired by the project owner. It is not a
  gate for these scenarios.
- Check the Platform health endpoint and that `virtual-base` is online before
  starting. Confirm the local development or mission endpoint required by the
  selected workflow is enabled. If a stack reload is required, the operator
  performs it under the workspace's reload rule.
- Use separate browser profiles or devices for the participating Console
  accounts. Do not share an authenticated browser session when testing
  identity boundaries.
- Use the test workspace and test mission only. Save IDs and outcomes, never
  access tokens, device secrets or local grants.

## Human accounts and workspace setup

Use at least five Auth0 users, each signed in once so Ringfour has created its
local user record:

| Alias | Workspace membership | Role | Use |
|---|---|---|---|
| Owner A | Workspace A | Owner | Provision members and inspect history |
| Author B | Workspace A | Developer | Start the mission; expected initiator/Logbook actor |
| Operator C | Workspace A | Operator | Respond to an interaction |
| Operator D | Workspace A | Operator | Compete with Operator C and verify replay/conflict |
| Outsider E | Workspace B only | Owner or Operator | Check cross-workspace isolation |

Create Workspace B with Outsider E if there is not already a suitable separate
workspace. Do not add Outsider E to Workspace A. Owner A can invite Author B,
Operator C and Operator D by email from Console Organizations, choosing each
role. Each invitee signs in or creates an Auth0 account with that verified
email, then accepts the invitation. For API-level setup, the owner-only
membership endpoint remains available. Each participant can get their local
user ID from Console Organizations or `ringfour-cli whoami`.

```http
POST /v1/organizations/{workspace_a_id}/members
Authorization: Bearer <owner-a-access-token>
Content-Type: application/json

{"user_id":"<participant-user-id>","role":"operator"}
```

Use `developer` for Author B. The target user must have signed in before being
added. Verify the final roster with Owner A and record workspace, user and role
IDs in the run notes. Do not put bearer tokens in command history or evidence.

The role hierarchy is Owner ≥ Developer ≥ Operator. Each is authorized for the
history and interaction read/respond operations in these scenarios. There is
currently no lower workspace role to use as an in-workspace denial case; use
Outsider E for the non-member boundary.

## Shared test flow

Author B publishes and starts the no-motion HITL demo from a terminal:

```sh
ringfour-cli mission push ../ringfour-missions/hitl-demo/ \
  --robot virtual-base --start --follow
```

Keep that terminal open. The mission prints each request UUID, prompt and
allowed choice values. Use the request UUID and exact values printed by the
mission; do not invent a choice. Operators C and D use separate Console browser
profiles. Owner A can inspect concurrently. Use the commands below in a CLI
authenticated as the relevant operator when a CLI response is part of a case:

```sh
ringfour-cli hitl list --org <workspace-a-id> --state pending --robot <robot-id>
ringfour-cli hitl show <request-id>
ringfour-cli hitl respond <request-id> <choice-value> \
  --idempotency-key <stable-test-key>
```

The mission asks three questions in sequence, so finish each interaction before
testing the next one. Use the safe `cancel` option for the final approval prompt
unless the scenario specifically tests the other listed choices. A timed-out
interaction takes its mission-defined safe outcome and does not authorize any
action.

## Milestone 08 — shared Logbook and actor context

### 08-MU-01: Same-workspace visibility and actor identity

1. Author B starts `hitl-demo`; Operator C answers the first question.
2. Open Console Logbook as Author B, Operator C and Owner A. Select the same
   workspace and execution.
3. Confirm all three see the same ordered mission-authored entries after the
   response, including **Human interaction resolved** and its structured
   outcome fields. The running execution timeline should refresh without
   requiring a full page reload.
4. Compare the actor for those author entries. The current contract attributes
   them to the execution initiator (Author B), not the person who selected the
   HITL choice. Author B may appear as **You**; other users see the actor's user
   ID. Do not expect a friendly display name.

**Pass when:** authorized members see the same organization-scoped events, the
execution association is correct, and the actor agrees with the originating
execution. Record the HITL responder separately from the Logbook actor.

### 08-MU-02: Filters and current membership role

1. In Logbook, filter to the demo execution and then filter by Human.
2. Select Author B. The entries authored during this execution should remain;
   filtering for Operator C should not recast those entries as Operator C's
   author events.
3. As Owner A, change Author B's Workspace A role from Developer to Operator
   through the owner membership API, then reload the same history as Operator C.
4. Confirm that stored events are unchanged and the UI's `role (current)` label
   reflects Author B's current role. This field is not a historical role
   snapshot. Restore the original role after the check if later cases need it.

**Pass when:** filters select the requested actor and current role presentation
does not change event attribution or content.

### 08-MU-03: Cross-workspace isolation

1. As Outsider E, select Workspace B and inspect its Logbook. Workspace A's
   mission entries must not appear.
2. Attempt to request Workspace A history by its ID using Outsider E's
   authenticated session. The API must return the standard inaccessible
   resource response (`404 not_found`), with no event contents or actor IDs.
3. Confirm Workspace A's members can still read their events after this
   rejected request.

**Pass when:** no event or execution metadata crosses the membership boundary.

### 08-MU-04: Replication after a Platform outage

Use a planned test window and the existing stack. Do not run this while another
user is relying on Platform availability.

1. Start the demo and arrange for an author entry to be written while the daemon
   cannot reach the Platform but Runner and the local daemon remain available.
2. Confirm the mission receives local durable acceptance and continues waiting
   for its next human choice; do not report the event as hosted before upload.
3. Restore Platform connectivity. As Operator C, refresh Logbook and confirm
   the entry appears once, with the same event ID, execution and Author B actor.
4. Restart or replay delivery only if required by the outage procedure; verify
   replay does not duplicate the row.

**Pass when:** the event is not lost or duplicated and the original actor and
execution context survive the outage.

### Milestone 08 known boundary

The Logbook records mission-authored entries and selected Agent motion records.
HITL request, confirmed-answer, timeout and cancellation entries are generated
from hosted interaction state; request entries use the execution initiator,
confirmed answers use the responding operator, and automatic timeout or
cancellation entries have no human actor. The HITL demo may also write a
mission-authored resolution entry attributed to its initiator; keep that distinct
from the hosted answer event. Production mission revision-selected/start/
completion/failure/stop entries are now generated from durable execution state.
Record missing automatic events as **Not supported**, not **Pass**.

## Milestone 09 — multi-operator response and isolation

### 09-MU-01: Workspace members inspect the same pending request

1. Start a fresh `hitl-demo` execution as Author B and wait for its first
   question.
2. Open Overview as Operator C and Operator D. Both should see the same pending
   prompt, robot, deadline and exact choices. Owner A should be able to inspect
   it too. Developer B should also be able to inspect the interaction.
3. Use `ringfour-cli hitl show <request-id>` as Operator C to capture the
   hosted state before answering. It should be `pending` with no outcome.
4. On separate fresh requests, have Developer B and Owner A each submit one
   listed response. Both roles are above the minimum Operator threshold and
   should receive `submitted`; verify eventual device confirmation in the
   following cases.

**Pass when:** each Workspace A member can inspect the same request and no
response or approval is implied by mere visibility.

### 09-MU-02: Competing operators and first-winner semantics

1. Use a fresh pending request and have Operators C and D submit different
   valid choices as nearly simultaneously as possible from their separate
   authenticated sessions.
2. Exactly one response should be accepted (`202 submitted`). The other should
   receive a clear conflict. The persisted response must identify the winning
   `actor_user_id` and choice.
3. Retry the winner's exact choice with the same `Idempotency-Key`; it should
   return the original response ID. Retry with a different choice, key or
   losing operator; it must not replace the winner.
4. Wait for device confirmation. `hitl show` must progress from submitted to
   `answered`, the chosen outcome, and response status `applied`. The mission
   should continue once with that outcome.

**Pass when:** one operator wins, retries are idempotent, no second choice is
applied, and the winner is explicitly recorded.

### 09-MU-03: Console submission and confirmation

1. As Operator C, choose one allowed response in the Overview queue.
2. Confirm the card indicates that the response is submitted and waiting for
   device confirmation; it must not claim the mission has already applied it.
3. Refresh or reopen Overview. Confirm there is no second pending action for
   the submitted interaction. Use `hitl show` to inspect the eventual device
   outcome and response status.

**Pass when:** Console state distinguishes hosted submission from daemon
confirmation and a page reload does not create a second response.

### 09-MU-04: Timeout, late response and cancellation

Run separate fresh requests for each branch:

- **Timeout:** do not answer before the deadline. The mission must resume only
  with its explicit safe `timeout_outcome`; `hitl show` reports `timed_out`.
  A later response is rejected and cannot change the outcome.
- **Late hosted submission:** where the API still has an offline provisional
  submission awaiting device confirmation, let the daemon's local deadline
  expire before it reconciles. The daemon's `timed_out` result wins and the
  response becomes `rejected_timed_out`; it must never become `applied`.
- **Cancellation:** stop the waiting mission using its normal Ringfour stop
  workflow. The interaction becomes `cancelled`, no response is applied, and
  the mission does not continue past the wait.

**Pass when:** exactly one terminal state/outcome is recorded and no timeout or
cancellation branch is presented as approval.

### 09-MU-05: Cross-workspace denial

1. While an interaction in Workspace A is pending, use Outsider E's authenticated
   Console profile to inspect Workspace B's Overview. The Workspace A request
   must not appear.
2. Attempt `ringfour-cli hitl list --org <workspace-a-id>` and
   `ringfour-cli hitl show <request-id>` as Outsider E.
3. Both requests must be inaccessible (`404 not_found`); no prompt, choices,
   robot, execution, response or actor information may leak. Confirm Operator C
   can still inspect and respond afterward.

**Pass when:** workspace membership is checked for list, detail and response
operations independently.

### 09-MU-06: Responder attribution versus Logbook attribution

1. Complete one question as Operator C and record the interaction's
   `response.actor_user_id` from `hitl show`.
2. Inspect the associated Logbook author entry as Operator D.
3. Record both identities separately. Current mission-authored Logbook entries
   attribute to Author B, the execution initiator; the hosted interaction stores
   Operator C as response actor. The current 09 evidence lists Logbook
   attribution for requests and responses as an incomplete gate.

**Pass condition:** interaction response records the correct responder and the
hosted `human.interaction.answered` Logbook event names that same user as actor.
The mission-authored resolution entry may still name the execution initiator;
do not confuse that author event with the operator's hosted response event.

## Results and evidence

For each run, record:

- date, API/Console/daemon/Runner versions or image IDs, and that the existing
  live stacks were used;
- workspace IDs and test aliases/user IDs/roles (omit email addresses unless
  needed for diagnosis);
- robot, mission revision, execution, interaction, response and Logbook event
  IDs relevant to each case;
- status for each case, exact observed HTTP/CLI/UI result, and evidence path;
- any service interruption, cleanup, role restoration and remaining open
  interactions.

Redact access tokens, refresh tokens, robot credential secrets and local grant
material. Attach this plan's dated result or link to it from
`docs/evidence/08.md` and `docs/evidence/09.md`. Do not mark either milestone
complete based on the multi-user cases alone: their remaining single-user,
recovery, contract, migration and implementation gates still apply.

## Run record template

```text
Date/time:
Existing stack image IDs/versions:
Workspace A / Workspace B:
Owner A / Author B / Operator C / Operator D / Outsider E user IDs and roles:
Robot / mission revision / execution:
Case results (08-MU-01..04, 09-MU-01..06):
Interaction and response IDs:
Logbook event IDs:
Observed differences or failures:
Cleanup and remaining open state:
Evidence location:
```
