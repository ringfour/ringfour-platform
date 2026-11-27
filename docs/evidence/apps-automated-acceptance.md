# Apps automated acceptance runner — 26 November 2026

The runner and [operator guide](../human-testing/apps-automated-acceptance.md)
automate repeatable counterparts of the final human acceptance checklist.
Quick/full/integration profiles use component and disposable process/browser
fixtures. The live profile requires explicit organization/robot/repository
inputs, uses the caller's real CLI Auth0 identity, and never performs stack
operations. Read-only preflight is separate from live App mutations.

Validated with Node 24.22.3, .NET SDK 10.0.115, uv 0.13.8, pytest 9.2.0,
Pydantic 2.14.3 and the Console's locked Playwright/Vite dependencies.

## Executed checks

| Check | Command from Platform root | Result / evidence |
|---|---|---|
| Runner behavior | `npm run test:acceptance:runner` | 6 passed: real child exit/log, pending prerequisites, bounded interruption, explicit live opt-in and report distinctions |
| Live-runner safety | `uv run --locked --project apps/cli pytest apps/cli/tests/test_apps_acceptance_live.py` | 4 passed with fake HTTP/processes: read-only preflight, busy/ambiguous refusal, no token in report and own-ID cleanup after child failure |
| Component sweep | `python3 scripts/apps-acceptance.py --profile quick --output /tmp/rf-apps-acceptance-final-quick` | All five suites passed: contracts, Console typecheck and 95 tests, CLI 73 tests, Daemon 114 tests. [Report](apps-automation/quick.json) |
| API/Postgres | `python3 scripts/apps-acceptance.py --suite daemon --suite api --suite browser --output /tmp/rf-apps-acceptance-integration-20261126` | Daemon and all 171 API tests passed; this initial browser attempt failed on a stale table expectation. Preserved [initial report](apps-automation/initial-api-and-browser.json) |
| Browser source fixtures | `LD_LIBRARY_PATH=/tmp/rf-browser-libs/extracted/usr/lib/x86_64-linux-gnu python3 scripts/apps-acceptance.py --suite browser --output /tmp/rf-apps-acceptance-browser-v4` | Passed; 38 screenshots and interaction assertions. [Report](apps-automation/browser.json). Screenshots in `/tmp/rf-apps-acceptance-browser-v4/browser/` |
| Disposable lifecycle | `python3 scripts/apps-acceptance.py --suite ap18-lifecycle --output /tmp/rf-apps-acceptance-protocol-20261126` | Passed real Kestrel/PG/registry/Daemon/engine/CLI scenarios: install/configure/update/rollback, official discovery by separate test principals, private isolation, uninstall, retained history, fresh reinstall identity and untouched other installation. [Report](apps-automation/lifecycle.json) |
| Disposable settings/runtime | `python3 scripts/apps-acceptance.py --suite ap17-settings --output /tmp/rf-apps-acceptance-settings-v2` | Passed fractional startup/live settings, raw NaN rejection, CAS and App rejection, result-ack loss/recovery, pending-stop isolation and scoped verified files. [Report](apps-automation/settings.json) |
| Real Auth0 read-only preflight | `python3 scripts/apps-acceptance.py --profile live --live-preflight --live-org 88d119d4-8f51-42dc-b1fd-65376f6b13b3 --live-robot virt2 --output /tmp/rf-apps-acceptance-final-preflight` | Passed identity, organization-scoped unambiguous robot lookup, online/idle state and uninstall support. No install/start/stop. [Runner report](apps-automation/live-preflight.json), [details](apps-automation/live-preflight-details.json) |

Python compilation, Ruff checks on the new runner/safety tests and JavaScript
syntax checks passed. Reports contain commands/IDs and results, not bearer
tokens or keyring contents. Raw logs remain in their original output directories.

## Issues exposed and corrected

1. Daemon status parsing rejected five positive fixtures because the typed
   `RobotAppStatus` reader lacked the existing contract's
   `supports_app_uninstall` field. Added its optional typed boolean; all 114
   Daemon tests then passed. This consumes an existing contract, not a new API.
2. Browser assertions still expected a robot table and open sections. Updated
   them for the requested robot cards and actual side padding. The checks
   exposed the Installation management section's old open-section CSS removing
   its side padding. Removed that exception; the browser suite passed and the
   resulting robot screen was inspected.
3. AP-17 asserted stdout immediately after the independent settings projection
   confirmed a fractional value. The first run failed before stdout delivery.
   Changed the assertion to wait for the actual handler event with the existing
   bounded wait helper. The rerun passed, including the raw NaN negative case;
   no expected value or protocol requirement was relaxed.

Browser libraries were extracted from Ubuntu packages under
`/tmp/rf-browser-libs` and passed through `LD_LIBRARY_PATH`; no system package
or live stack was restarted. The source-preview server and disposable test
resources were cleaned up. The management-card CSS fix takes effect in the
deployed Console after an operator-managed Console reload.

## Remaining gates

No full `--profile full` run is claimed: the component sweep, API, browser,
AP-18 and AP-17 jobs above were executed individually. Other registered
process suites can run through the same runner but were not all rerun here.

**Mutating live workflows have not run.** The user is preparing a second robot
under ring1's separate organization. Required next inputs are that organization's
UUID and robot UUID, plus both users' working CLI sessions and idle targets.
Live IMU/settings/lifecycle, cross-user shares/foreign IDs/roles, real browser
Auth0 login/logout, concurrent two-robot continuity and hosted/second-platform
gates remain pending until their corresponding real evidence exists.

The live scripts record progress IDs atomically and attempt cleanup only of
their recorded run/installation IDs. Operator-only live recovery/recreation
checks remain manual under `AGENTS.md`. Disposable recovery passing does not
authorize a Platform or ringfour-virtual reload. No live data was marked passed
from browser mocks or isolated signing keys.


## Control-room full sweep follow-up

The UI run `26ff91a3-215c-491f-853d-67354f522ad2` completed with 12 passing
suites, failed AP-06 publish, and pending browser prerequisites. Its original
report is preserved at `/tmp/ringfour-acceptance-g0tgc5wf/report.json` and in the
control-room run history; it is not rewritten as a pass.

- AP-06 assumed the initialized App release was `0.1.0`, but the pinned template
  now initializes `0.2.0`. The harness reads `app.json` for the frozen release
  filename and increments that release's patch number for its distinct revision.
- Once that blocking assertion was fixed, the harness reached another stale
  expectation: default private publication appearing in Store search. AP-18
  restricts Store discovery to listed official releases. The check now requires
  `unlisted` visibility, positive publisher App/revision reads, and absence of
  the private App from the Store. An intermediate check mistakenly used the UI
  word `private` instead of the wire enum `unlisted`; this was corrected against
  the actual CLI/API contract. Existing image, retry, digest and immutable-history
  checks remain in place.
- The UI worker lacked the shell-only Chromium library export, so launch failed
  on `libnspr4.so`. Existing extracted libraries were copied to the cdr user's
  durable `~/.local/share/ringfour-acceptance/browser-libs` cache. The browser
  script now uses that optional directory, or `RINGFOUR_BROWSER_LIBRARY_PATH`,
  and passes it through to child browser processes. No system packages or live
  stacks were changed.

The first focused follow-up is `/tmp/rf-acceptance-publish-browser-fix-20261126`:
its browser suite passed (38 screenshots), while AP-06 exposed the outdated
Store expectation. The second AP-06 run at
`/tmp/rf-acceptance-publish-fix-v2-20261126` failed on the incorrect new visibility
enum assertion; it remains failed evidence. The final corrected AP-06 follow-up
is `/tmp/rf-acceptance-publish-fix-v3-20261126` (result recorded below).

Final AP-06 result: **passed**, including real Docker build/push, clean-engine
pull by digest, API-offline frozen retry, private publisher reads/Store isolation,
distinct next revision, preserved original snapshot/metadata, and committed replay
with the registry stopped. [Corrected publish report](apps-automation/publish-corrected.json).
Browser result: **passed**, all 38 screenshots, with no shell library export.
[Focused report preserving the earlier publish failure](apps-automation/publish-browser-follow-up.json).
Only the two affected suites were rerun; the original full sweep's other 12
passes were not re-executed. No live robot workflow or stack operation was run.
Python compilation/Ruff, browser JavaScript syntax, and `git diff --check` passed.


## Verbose output and live suite progress

The runner now enables spec/verbose reporters for contracts, Console, CLI and
Daemon, and detailed API console results. It streams child lines with UTC time
and suite ID, prints commands/directories/prerequisites/timeouts and final exit
codes/durations, and emits a heartbeat after ten quiet seconds. Console relay
redacts bearer credentials and JWTs. A running report entry is written before
execution; final entries retain start/finish timestamps and elapsed seconds.
The control room displays each suite's timing, exit code and expandable output,
while the combined Logs stream retains runner diagnostics. Running suites do not
count as completed in the progress indicator.

Validation:

- `python3 -m unittest discover -s tests/automation -v`: **7 passed**. The new
  regression executes a real child that waits for its output to be relayed
  before exiting, proving live delivery and credential redaction; existing
  pending/failure/timeout/cleanup checks also pass.
- `python3 scripts/apps-acceptance.py --profile quick --suite contracts --suite console-types --suite console --suite cli --suite daemon --suite api --output /tmp/rf-acceptance-verbose-20261126`:
  **all 6 selected suites passed**, exercising the actual detailed reporter
  arguments and live relay. [Report](apps-automation/verbose.json). Combined
  timestamped output: `/tmp/rf-acceptance-verbose-20261126.log`.
- Standalone UI `LD_LIBRARY_PATH=/tmp/rf-browser-libs/extracted/usr/lib/x86_64-linux-gnu node tests/browser-smoke.mjs`:
  **passed**, now also verifying active-suite output before completion, expanded
  per-suite logs and a progress value of zero while the only suite is running.
- Ruff, JavaScript syntax checks and `git diff --check`: **passed**.

No live robot test or Platform/virtual stack operation was performed. Existing
workers invoke the updated runner on their next job; refresh the browser to load
the new static UI without disconnecting those workers.


## Live preflight a61b7517: offline virt2

The UI live run `a61b7517-9a29-42c8-aaa5-e34ef4bcc6e6` stopped before any
App mutation: the selected virt2 API status was `offline`, last seen
`2026-11-26T13:20:43.073547Z`, with support `not_negotiated` and null uninstall
capability. Docker inspection showed `ringfour-virtual-daemon-1` in Created
state, with start failure `Bind for 127.0.0.1:8083 failed: port is already allocated`.
`ringfour-server-adminer-1` owns that host port.

The singleton's local `.env` now selects `RINGFOUR_STATUS_HOST_PORT=8084`;
read-only Compose configuration confirms the 8084→8083 status mapping. No
container was started/recreated/reloaded by the agent. The operator must run
the virtual stack command under the workspace rule, then rerun read-only
preflight. The primary App runtime port and second robot's ports are unchanged.

Preflight diagnostics now distinguish an offline robot (including name, UUID
and last seen) from an online robot lacking uninstall negotiation. Both failures
persist an explicit readiness/error report with `mutations: false` before exit.
CLI safety checks: `uv run --locked --project apps/cli pytest apps/cli/tests/test_apps_acceptance_live.py -q`:
**6 passed**, including both new refusal paths; Ruff passed. These component
checks do not establish successful robot reconnection or a passing live workflow.


## Adminer port separation

Changed Adminer's loopback host port from 8083 to **9080** in the Compose fallback,
`.env.example` and the existing local `.env`. Updated the current operator links
and virtual-stack notes. The singleton's existing 8084 override is retained;
9080 is outside all supported numbered virtual robot ports. Historical collision
evidence above remains unchanged.

Read-only `docker compose -f infra/server/docker-compose.yml --env-file .env config --format json`
validation resolved Adminer to `127.0.0.1:9080 → 8080/tcp`. `git diff --check`
passed. No Adminer or Platform service was recreated by the agent. Under the
workspace rule, the operator must apply only Adminer from the Platform root:
`./up.sh -d --no-deps adminer`, then use `http://localhost:9080`.
