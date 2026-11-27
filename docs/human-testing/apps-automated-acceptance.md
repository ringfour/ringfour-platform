# Automated Apps acceptance

The standalone UI is in `/home/cdr/ringfour/ringfour-acceptance`. It provides
account/organization/robot selection, sequential worker jobs, progress, logs and
saved reports around this CLI runner. See the sibling project’s `README.md` for
startup and connecting a worker from each user’s shell. The commands below remain
available directly; the UI does not replace the test suites.

Run from `/home/cdr/ringfour/ringfour-platform`. The runner uses the existing
component and process suites, runs them sequentially, and writes `report.md`,
`report.json` and individual logs under a fresh ignored `test-results` folder.
An explicit `--output DIR` preserves a report at a location you choose; an
existing report is never overwritten. No Platform or ringfour-virtual stack is
reloaded, restarted, recreated or rebuilt.

## Component and disposable suites

```sh
# Contracts, Console types/interactions, CLI and Daemon tests:
npm run test:acceptance

# Show all disposable suites without starting anything:
npm run test:acceptance -- --profile full --list

# Also run API/Postgres, catalog, CLI publish, install/configure, hosted runs,
# runtime evidence, live settings, lifecycle recovery and browser fixtures:
npm run test:acceptance -- --profile full

# Or select individual suites (repeat --suite to combine):
npm run test:acceptance -- --suite api --suite ap18-lifecycle --suite browser
```

Prepare each component's locked dependencies first (`npm ci` in the Node
packages, `uv sync --locked` in CLI/Daemon and restore the API). Process suites
need working Docker, the existing prebuilt `ringfour-agent:local` image, and
sibling Agent/SDK checkouts. Missing prerequisites are **pending**, never pass.
The runner does not build/reload an existing stack to satisfy a prerequisite.

Browser fixtures use a separate temporary Vite source-preview server, synthetic
Auth0/API responses, Playwright and screenshots. Prepare Chromium and its
system dependencies in `apps/console` with
`npx playwright install --with-deps chromium`. They are component/browser
evidence, **not live authenticated E2E**. The isolated process suites use real
Kestrel/Postgres/registry/Daemon/engine components and Development-only signing
keys in disposable fixtures; they do not use or replace your live Auth0 login.

For Linux environments with user-local Chromium libraries, the browser runner
also checks `~/.local/share/ringfour-acceptance/browser-libs`, or an explicit
`RINGFOUR_BROWSER_LIBRARY_PATH`. It prepends that directory to `LD_LIBRARY_PATH`
for Chromium and browser child processes. This also works when a UI worker was
started without a shell-specific library export. Each account needs installed
system dependencies or its own readable library directory; missing dependencies
remain pending. No libraries are downloaded or installed by a test run.

Exit codes: **0** selected suites passed, **1** at least one failed, **2** a
selected suite is pending/missing prerequisites, **130** interrupted. The JSON
report always keeps `full_live_acceptance: false`; checkbox coverage lists the
remaining human/live evidence. A passing quick profile is not the full sweep.
Interrupted processes receive SIGINT so their own disposable cleanup can run.
After a hard timeout inspect the log and any reported leftover fixture resources.

## Output and progress

Verbose output is enabled by default for both direct CLI runs and UI workers.
The runner prints each suite's position, prerequisites, exact command, working
directory, timeout and log location. Child output is relayed while the process
is running, with UTC timestamps and suite names. A check that has produced no
output for ten seconds gets an elapsed-time heartbeat. Final lines include the
outcome, duration and exit code; failures retain their tracebacks and log files.
Bearer credentials and JWTs are redacted from the relayed console output.

Python suites list individual tests and their ten slowest durations; Console
uses the verbose Vitest reporter, contracts use Node's spec reporter, and API
uses detailed console test results. The JSON report is written before a suite
starts with `status: running` and `started_at`, then replaced with its terminal
result, `seconds`, `finished_at` and `returncode`. Running suites do not count as
completed in UI progress. Each suite has its own expandable output in the UI;
Logs contains the timestamped combined runner stream. Refresh the UI to load
these display changes; new runs pick up the runner changes without stack reloads.
Original log files remain in the worker evidence folder, with bounded UI previews.

## Live robot workflows

These are opt-in and run as the current Unix user's real CLI identity. They
require a selected organization, an explicit robot name/UUID and, for mutations,
a registry repository prefix. The prefix gets a unique per-run suffix. They
refuse foreign/ambiguous robots, offline devices, active runs and devices
without uninstall support. No motion or device reconfiguration is issued.

Use the prepared workspace CLI so the two accounts run the same source version:

```sh
apps/cli/.venv/bin/ringfour-cli whoami
apps/cli/.venv/bin/ringfour-cli org list
apps/cli/.venv/bin/ringfour-cli robot list
```

The live profile reads the prepared CLI Python environment without updating it;
each Unix user still uses their own config/keyring. Do not copy tokens between
accounts. The checkout/environment must be readable and Docker available to the
user; select a writable `--output`, e.g. a fresh directory under `/tmp`.

Use the two-workspace arrangement from the human checklist:

| Shell/account | Organization | Robot |
|---|---|---|
| cdr | kraftwerk (`88d119d4-8f51-42dc-b1fd-65376f6b13b3`) | virt2 (`91bc543a-db5f-4b90-bb58-5b7abe17a4cd`) |
| ring1 / Outlook | A separate consumer organization owned by ring1 | The second disposable robot |

Do not grant cdr membership in the consumer organization before the foreign-ID
checks. Robots are organization-owned, not directly user-owned.

First, as cdr, run the read-only preflight:

```sh
python3 scripts/apps-acceptance.py --profile live --live-preflight \
  --live-org 88d119d4-8f51-42dc-b1fd-65376f6b13b3 \
  --live-robot 91bc543a-db5f-4b90-bb58-5b7abe17a4cd
```

Then explicitly run the mutating workflow on that idle test robot:

```sh
python3 scripts/apps-acceptance.py --profile live \
  --live-org 88d119d4-8f51-42dc-b1fd-65376f6b13b3 \
  --live-robot 91bc543a-db5f-4b90-bb58-5b7abe17a4cd \
  --live-repository 127.0.0.1:5000/ringfour/acceptance \
  --output /tmp/apps-live-cdr-01
```

The local registry prefix above is for the documented local Docker Desktop
setup. For another engine/network use a prefix already reachable for push,
API verification and robot pulls. The workflow publishes a private reference
Robot Check App, installs it, verifies real IMU samples/live settings and its
summary bytes, checks running update/uninstall refusal, exact/stale stop,
idle update, rollback, retained evidence after removal and a fresh reinstall
identity. It verifies pre-existing installation bindings are unchanged.

Cleanup stops only run IDs created by this workflow and uninstalls only its
test installations. Private test revisions and their immutable run/artifact
history remain available as evidence. Failures and cleanup errors keep the
overall result failed. `live-workflow.json` and `live-imu-settings.json` record
exact IDs incrementally, so failed runs can be diagnosed without guessing.

Repeat from ring1's shell, replacing both IDs and using a separate output:

```sh
apps/cli/.venv/bin/ringfour-cli org select CONSUMER_ORG_UUID
python3 scripts/apps-acceptance.py --profile live --live-preflight \
  --live-org CONSUMER_ORG_UUID --live-robot SECOND_ROBOT_UUID \
  --output /tmp/apps-live-ring1-preflight-01
python3 scripts/apps-acceptance.py --profile live \
  --live-org CONSUMER_ORG_UUID --live-robot SECOND_ROBOT_UUID \
  --live-repository 127.0.0.1:5000/ringfour/acceptance \
  --output /tmp/apps-live-ring1-01
```

Two independent passing runs are useful live evidence. They do **not** by
themselves pass cross-user shares/foreign IDs, concurrent two-robot continuity,
real browser login/logout, invitations/role changes or subjective visual review.
Use the saved IDs with the remaining [human checklist](apps-pivot-acceptance.md).
The section-6 **live** restart/recreation scenarios remain operator-only under
`AGENTS.md`; their disposable process counterparts can run automatically.

## Evidence and runner checks

The runner's own tests cover failure exit codes/logs, pending prerequisites,
timeout cleanup, report distinctions and refusing implicit live mutations:

```sh
npm run test:acceptance:runner
uv run --locked --project apps/cli pytest apps/cli/tests/test_apps_acceptance_live.py
```

The latter uses a fake transport to prove preflight has no mutations, busy and
ambiguous targets are refused, and failure cleanup uses only recorded own IDs.
See [automation validation](../evidence/apps-automated-acceptance.md) for the
actual runs performed during implementation and remaining gates.
