# Ringfour Python runtime clients

This optional, dependency-free Python package supplies App runtime clients for
Logbook entries, human decisions, artifact publication and live settings. It installs
independently of `ringfour-sdk` and the Agent. Robot-control calls use the
separate SDK/Agent connection.

## Install the local wheel bundle

Platform's [bundle builder](../../docs/python-distribution.md) produces a
versioned local wheel bundle. From its extracted `linux-amd64` directory:

```sh
uv venv --python 3.13 .venv
uv pip install --python .venv/bin/python --no-index --find-links wheels \
  --require-hashes --only-binary=:all: -r requirements-runtime.txt
.venv/bin/python -c "from ringfour_runtime import artifacts, interactions, logbook, settings"
```

The [bundle release](../python-artifacts/release.json) pins runtime
`0.1.1.dev0` (including live settings), SDK `0.1.0.dev0` and Agent protocol 1.

## Use from an App

The enrolled Daemon injects per-run runtime URLs and a scoped credential. App
authors do not set or persist those credentials. The same public client calls
work for Logbook and human decisions:

```python
from ringfour_runtime import interactions, logbook

logbook.info("Inspection skipped", station="B", reason="Maintenance")
request = interactions.create(
    "Continue the inspection?",
    {"continue": "Continue", "abort": "Abort safely"},
)
request = interactions.wait(request.request_id)
```

Apps that declare live settings can poll, validate, apply and acknowledge full
settings revisions with the run-scoped Daemon client:

```python
from ringfour_runtime import settings

def validate(values):
    if not 100 <= values["sample_interval_ms"] <= 5000:
        raise settings.SettingsRejected("Sample interval must be 100–5000 ms.")

def apply(values):
    sampler.set_interval(values["sample_interval_ms"])

settings.serve(apply, poll_seconds=2, validate=validate)
```

`serve` retries transient runtime outages and retains each completed result in
memory until the Daemon acknowledges it. A process restart can cause a handler
to run again if the result never reached the Daemon, so handlers should make
their effects idempotent. Use `SettingsRejected` for a safe, single-line reason;
other exceptions remain application failures. Pass a `threading.Event` as
`stop_event` to cancel polling cleanly.

An API response is provisional until the Daemon confirms application. Stop,
timeout, offline and competing-response outcomes remain distinct. See
[App runtime and evidence](../../docs/apps-runtime.md) for credential, replay,
retention and authorization behavior.

Publish an immutable artifact from the configured artifact directory:

```python
from ringfour_runtime import artifacts, logbook

artifact_id = artifacts.publish_file("inspection/photo.png", content_type="image/png")
logbook.info("Inspection complete", evidence=[artifact_id])
```

The Daemon snapshots bytes before acknowledging locally; delivery can resume
after API or Daemon interruption. Use relative paths inside the configured
artifact directory and do not include credentials in artifacts or Logbook
fields.

## Build from source

From the Platform repository root:

```sh
SOURCE_DATE_EPOCH=$(git log -1 --format=%ct -- packages/python-runtime) \
  uv build --wheel --build-constraints packages/python-runtime/build-constraints.txt \
  packages/python-runtime
```

Retain the source commit, build epoch, backend versions and wheel checksum.
