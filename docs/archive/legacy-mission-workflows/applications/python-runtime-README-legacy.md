# Ringfour Platform runtime client

This optional, dependency-free Python package provides Logbook, human
interactions and artifact clients. It installs independently of `ringfour-sdk`.
No ROS, SDK, Runner or Platform checkout is needed to import it. Calls require
a configured execution runtime and run-scoped credentials.

## Versioned local wheel installation

Platform's [bundle builder](../../../python-distribution.md) uses `uv` to
produce a self-contained, versioned local wheel bundle. From its extracted
`linux-amd64` directory:

```sh
uv venv --python 3.13 .venv
uv pip install --python .venv/bin/python --no-index --find-links wheels \
  --require-hashes --only-binary=:all: -r requirements-runtime.txt
.venv/bin/python -c "from ringfour_runtime import artifacts, interactions, logbook"
```

This installs the optional runtime alone, with no SDK dependency. The wheel
requires Python >=3.10; CPython 3.13 on Linux amd64 is exercised in AP-04.
Distribution version `0.1.0.dev0` is qualified by the bundle's wheel checksum;
earlier local development builds are not interchangeable. Never replace a
distributed bundle: changed package contents require a new distribution version.
Public package-feed/release publication is deferred.

## Runtime use

The existing mission path obtains runtime configuration through Runner.
Daemon-owned App execution credentials/delivery are an AP-10 gate; publishing
this package does not enable that new execution path. An execution can write
an intentional Logbook entry with:

```python
from ringfour_runtime import logbook

logbook.info("Inspection skipped", station="B", reason="Operator reported maintenance")
```

Install and hash-pin `ringfour-platform-runtime` in the mission's dependency
lock. It is independently packageable and does not depend on the standalone
`ringfour-sdk` or Agent. Robot SDK usage does not require this package. Infrastructure supplies the per-robot Daemon runtime URL and an
execution-scoped credential; if
the runtime is unavailable, `logbook.info` raises `LogbookError` instead of
claiming the entry was saved. Author fields should not contain secrets.

For local validation and deployment requirements, see the [Logbook contract](../contracts/logbook-contract.md)
and [human test guide](../human-testing/08.md).

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
See [milestone 09 evidence](../evidence/09.md) for implementation and
acceptance status.


## Artifacts and execution configuration

```python
from ringfour_runtime import artifacts

artifact_id = artifacts.publish_file("/output/photo.png", content_type="image/png")
logbook.info("Inspection complete", evidence=[artifact_id])
```

`RINGFOUR_LOGBOOK_URL/TOKEN`, `RINGFOUR_INTERACTION_URL/TOKEN` and
`RINGFOUR_ARTIFACT_URL/TOKEN` are supplied by the execution owner, never stored
in the package or image. Interaction/artifact clients can fall back to the
Logbook URL/token. URLs address the Daemon; robot operations use the SDK's
separate connection to the Agent. `RINGFOUR_ARTIFACT_DIR` bounds publishable
files. A local acknowledgement means durable local acceptance, not confirmed
hosted delivery. Do not put credentials in author fields or artifacts.

## Build from source

```sh
SOURCE_DATE_EPOCH=$(git log -1 --format=%ct -- packages/python-runtime) \
  uv build --wheel --build-constraints packages/python-runtime/build-constraints.txt \
  packages/python-runtime
```

Run this from the Platform root. Build from a clean checkout and retain the
source commit, epoch, backend dependency versions and wheel checksum. AP-04
records package acceptance separately from the AP-10 execution migration.
