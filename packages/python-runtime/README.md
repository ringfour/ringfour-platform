# Ringfour Platform runtime client

This optional Python package lets a mission write an intentional Logbook entry
when it runs under a configured Ringfour Platform Runner:

```python
from ringfour_runtime import logbook

logbook.info("Inspection skipped", station="B", reason="Operator reported maintenance")
```

Install and hash-pin `ringfour-platform-runtime` in the mission's dependency
lock. It is independently packageable and does not depend on the standalone
`ringfour-sdk` or Agent. Robot SDK usage does not require this package. The
Runner supplies the local runtime URL and an execution-scoped credential; if
the runtime is unavailable, `logbook.info` raises `LogbookError` instead of
claiming the entry was saved. Author fields should not contain secrets.

For local validation and deployment requirements, see the [Logbook contract](../../docs/logbook-contract.md)
and [human test guide](../../docs/human-testing/08.md).

## Human interactions

Milestone 09 adds the separate `interactions` client. Creating the request is
durable on the robot before it returns. By default, a request has no deadline
and `wait` stays blocked until a person responds or the mission stops. The wait
loop retries transient daemon/transport outages, so a daemon restart alone does
not make the mission abandon its pending request.

```python
from ringfour_runtime import interactions

item = interactions.create(
    "Continue the inspection?",
    {"continue": "Continue", "abort": "Abort safely"},
    request_id="8ad8de90-65d7-43f5-b2be-8d0639b62e32",
)
item = interactions.wait(item.request_id)
```

Timeouts are opt-in: pass both a timezone-aware `deadline` and a safe
`timeout_outcome` choice to `create`. When a deadline expires, the daemon records
`timed_out`; `wait` then invokes the optional `on_timeout(interaction)` callback
once and returns the terminal interaction. The callback runs inside mission
code and may log, raise, or select a mission-specific recovery path. The
daemon-authoritative interaction outcome remains the configured
`timeout_outcome`.

```python
from datetime import datetime, timedelta, timezone

def on_timeout(item):
    logbook.info("Operator response timed out", request_id=item.request_id)

item = interactions.create(
    "Continue the inspection?",
    {"continue": "Continue", "abort": "Abort safely"},
    deadline=datetime.now(timezone.utc) + timedelta(minutes=2),
    timeout_outcome="abort",
)
item = interactions.wait(item.request_id, on_timeout=on_timeout)
```

A hosted operator submission is provisional until the daemon confirms it.
See [milestone 09 evidence](../../docs/evidence/09.md) for implementation and
acceptance status.
