# Human interaction contract

Milestone 09 provides a Platform-specific mission runtime protocol. It is not
part of the robot SDK and does not grant motion authority.

## Authority and state

The mission chooses a stable UUID `request_id`, a bounded prompt and 2–12
unique choices. By default, it omits the deadline and timeout outcome; the
interaction remains pending until a human responds or the mission stops. A
mission may opt into a deadline by providing both an absolute deadline and a
`timeout_outcome` naming one of the choices. The timeout choice must be safe
without human approval; Ringfour can enforce that it is explicit, but cannot
infer application safety.

The daemon persists the request before acknowledging creation and owns an
explicit deadline when one was configured. Its state machine is:

```text
pending -> answered
        -> timed_out
        -> cancelled
```

All transitions use a conditional durable update from `pending`, so exactly one
terminal state wins. Reusing `request_id` with identical content is a replay;
different content or another execution is a conflict. The execution credential
supplies the execution and robot binding.

Stopping the execution cancels a pending interaction. Inability to reach Runner
does not fabricate cancellation, approval or timeout. An interaction without a
deadline remains pending during outages; one with a deadline resolves
`timed_out` locally when that deadline passes.

The SDK wait loop retries transient daemon and transport failures. A daemon
restart alone must not make the mission abandon a still-pending interaction.

## Hosted response semantics

The API accepts only a listed choice, from an organization operator, before an
optional hosted deadline. The first valid response wins atomically and an
`Idempotency-Key` replays that same submission. API acceptance means
`submitted`: it does not mean the robot received the response or that mission
code resumed.

When delivery occurs, the daemon conditionally chooses one result from its
local state and clock:

- before the deadline while pending: `answered`;
- at or after an explicit deadline: `timed_out` and the response is rejected;
- after mission stop: `cancelled` and the response is rejected.

The daemon reports that result to the hosted service. CLI and Console must show
the distinction between submitted and device-confirmed. A late answer cannot
replace a timeout even if it was accepted by the API while the robot was
offline.

## Logbook events

The Platform appends a `human.interaction.requested` entry when it first accepts
a daemon-synchronized request. Its actor is the verified execution initiator.
After the daemon confirms a submitted response, the Platform appends
`human.interaction.answered` with the actual operator as actor, the selected
choice label/value, and response ID. Daemon-confirmed timeout and cancellation
are also recorded with no human actor; Logbook queries must preserve that
explicitly empty actor instead of falling back to the execution initiator.
These hosted entries have
`local_sequence: null`; they do not consume or alter the daemon's contiguous
device outbox sequence. Event IDs are stable across retries. A provisional
response does not create an applied-answer entry.

## Limits and privacy

Prompts are at most 1000 characters, labels 160 characters, values 64
characters, and optional deadlines at most 24 hours ahead. Prompts and labels are
operator-visible data and must not contain secrets. Runtime request bodies are
limited to 16 KiB. Long polls are limited to 30 seconds.

The wire interfaces are frozen in
`packages/contracts/openapi/ringfour-runtime.v1.yaml` and
`packages/contracts/openapi/ringfour-api.v1.yaml`.
