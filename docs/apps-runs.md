# App runs

Start/stop use the App start/stop request, immutable run snapshot and device command
contracts. `POST /v1/app-installations/{id}/start` requires Operator access,
Idempotency-Key and the expected installed revision/configuration. The robot row
lock and shared execution slot serialize normal and development App runs.
Start commits the snapshot and durable operation together; HTTP 202 and device
acknowledgement mean accepted, never observed running.

`POST /v1/app-installations/{id}/stop` binds to expected_run_id. It may queue while
start is pending. It never selects a newer run. Duplicate keys return the original
outcome; an already terminal run can be stopped idempotently without an engine
mutation. A robot remains occupied during uncertain/offline delivery. Only a
terminal observation or definitely undispatched failure releases its slot.

Device observations use the existing monotonic, deduplicated report stream.
Installation and run reports share that stream. Run state can advance from
accepted to running or terminal (including fast exit/refusal); terminal records
cannot regress. Start success means an actual start was observed, including an
already exited process with a start timestamp. Stop before start cancels launch
and records stopped without creating a container. Replacement reconciles durable
intent; ambiguous start is never relaunched. Recheck fresh Agent capabilities,
engine target and installed generation before create. Do not repull on start.

Read routes are GET /v1/app-runs/{id} and the organization-scoped /app-runs list
with robot_id, installation_id and app_id filters. See [runtime delivery](apps-runtime.md) for output and evidence.
