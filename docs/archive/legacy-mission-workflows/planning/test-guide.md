# 08b handoff and test guide

Updated: 2026-11-17. This is a historical handoff for the 08b live-artifact work. The user retired physical-device acceptance for this project; the Base101 deployment and test instructions below are retained only as historical context and are not completion gates. Continue with local real-process and protocol acceptance.

Track current remaining milestone gates in the [acceptance checklist](acceptance-checklist.md); this handoff's historical status text may be superseded by later evidence.

## Current implementation

- A mission can call `ringfour_runtime.artifacts.publish_file(path, content_type="image/png")`. The runtime asks Runner to take an immutable execution-scoped copy and returns an artifact UUID after the daemon's SQLite queue is durable.
- The daemon uploads queued bytes to the Platform, retries after restart, persists an `uploaded` checkpoint, and asks Runner to remove its snapshot only after hosted upload succeeds.
- The API stores a stable artifact ID, MIME type, state, hash, size and path. Console polls artifacts for the selected execution and previews JPEG/PNG bytes fetched through the authenticated API. Other content types are download-only.
- Logbook request/event schema v2 adds an evidence array of artifact UUIDs. Existing v1 author entries remain supported; unresolved evidence can precede the artifact.
- `../missions/spin-photos/main.py` publishes each PNG and attaches its ID to its “Photo captured” entry.

These statements describe code and component coverage, not the whole acceptance chain. Items still requiring review or validation include full persisted DB mapping/migrations on PostgreSQL, object-store failure recovery, queue behavior across daemon and Runner restarts, production deployment wiring, image delivery through the rebuilt local stack, and the Base101 camera/motion run. No hardware or hosted deployment evidence has been recorded for 08b.

## Workspace caution

The worktree contains extensive pre-existing and in-progress changes from Milestones 06–08, including untracked files and generated items. Do not use `git clean`, `git reset`, broad checkout/revert commands, or stage/commit the whole worktree. Review `git status --short` and stage only reviewed 08b files when you choose to commit. The temporary Dockerfiles `apps/{runner,daemon,console}/Dockerfile.local-rebuild` are present to build from local base images that lack registry credentials. Keep them if useful for this environment; review before committing.

## Completed component checks

These passed on the current source snapshot:

```sh
(cd apps/runner && .venv/bin/pytest -q)                         # 34 passed
(cd apps/daemon && .venv/bin/pytest -q)                         # 80 passed
(cd packages/python-runtime && PYTHONPATH=src ../../apps/runner/.venv/bin/pytest -q) # 4 passed
(cd apps/console && npm test -- --run && npm run build)         # 32 passed; build passed
(cd packages/contracts && npm test)                             # 30 passed
bash scripts/dotnet.sh build
bash scripts/dotnet.sh build tests/Ringfour.Api.Tests/Ringfour.Api.Tests.csproj
```

The .NET builds succeeded. Full API xUnit execution was attempted but could not start PostgreSQL Testcontainers: its container client could not connect to the Docker socket from the .NET helper container. It is **not** a passing API test result. Existing daemon/Runner Python and Console tests do not substitute for this PostgreSQL gate.

## Local image state and rebuild

The initial E2E command tried to build using BuildKit and failed resolving the `ghcr.io/astral-sh/uv:0.12.1` base image through the local macOS credential helper. Docker has the API .NET base images and postgres locally. It does **not** have the `uv`, Python or Node base tags referenced by the standard Runner/daemon/Console Dockerfiles. Existing `ringfour-runner:latest`, `ringfour-daemon:latest`, and `ringfour-console:latest` were available, so locally derived 08b images were built by copying the changed source into those app images.

The images have since been rebuilt with `--no-cache --pull=false` using the locally available bases/previous app layers. The API was rebuilt from its Dockerfile and local .NET 10 SDK/runtime images. Runner and daemon source layers were recopied from the current source into the local app base images; Console source was recopied into the local Console app base. This is the best rebuild available without the missing pinned uv/Python/Node bases and registry credential access. It is not a clean build of Runner, daemon or Console from their standard Dockerfiles.

Current local 08b tag IDs observed after rebuild:

| Image | Tag | ID prefix |
|---|---|---|
| API | `ringfour-api:08b` | `03e5506a389b` |
| Runner | `ringfour-runner:08b` | `7effa9a9557d` |
| daemon | `ringfour-daemon:08b` | `769bd2cc9b5d` |
| Console | `ringfour-console:08b` | `c4c5445fb633` |

The spin-photos execution exposed a stale Platform runtime wheel in the Runner
image: the previous wheel contained `logbook.py` but not `artifacts.py`. The
runtime wheel has been rebuilt with SHA-256
`d0e666f1358f659cb42bfadc785d0f4b064b73495fae3da5234c6139ff95df34`, copied
into Runner's vendored wheel directory, and its hash in
`../missions/spin-photos/requirements.lock` has been updated. Runner and
Base101's Runner/daemon/Agent package images were rebuilt; the Balena staged
Compose build passed. The revised mission must be pushed again after the new
Balena release. No device deployment or mission run has yet verified this fix.

Those tags were also copied to `:latest` and `ringfour-{api,runner,daemon}:07-test` for the existing E2E script. Verify with:

```sh
docker image inspect ringfour-api:08b ringfour-runner:08b ringfour-daemon:08b ringfour-console:08b --format '{{.RepoTags}} {{.Id}}'
```

Never use these development/test API images or test signing mode for Base101.

## Next local acceptance attempt

The real-process test now has an added path to use the local runtime wheel and a small PNG, run it through the production execution interface, confirm the execution remains running, query linked evidence and artifact metadata, fetch bytes, check Runner snapshot cleanup, and request an orderly stop. The overall script also exercises development sessions and prior Logbook paths.

The first attempt stopped at the Docker BuildKit credential error. After local image tags were set, the second attempt was interrupted while the script was still running; its final result was not observed. Check whether its process and isolated Docker resources remain before retrying:

```sh
docker ps -a --format '{{.Names}} {{.Status}} {{.Image}}' | rg 'rf07-'
```

If stale `rf07-*` resources exist, inspect the exact names and state first. Remove only those belonging to the interrupted test. Then rerun:

```sh
RINGFOUR_TEST_SKIP_BUILD=1 RINGFOUR_TEST_LOCAL_AUTH=1 \
  RINGFOUR_TEST_LOGBOOK=1 bash tests/e2e/07-cli-dev.sh
```

The expected final output is a `PASS:` line describing live entries and image evidence. Investigate and fix any failures before treating the local acceptance as complete. The local test uses a synthetic one-pixel PNG and a test JWT; it proves transfer/display plumbing only, not a real camera, Auth0 login or motion safety.

Then rerun the isolated outbox and API migration/test gates using the exact commands in [Milestone 08 evidence](../evidence/08.md) and the component commands above. In particular, add a real PostgreSQL API integration test for live artifact replay, organization authorization, same-path/same-ID conflicts, object-store bytes and MIME type, v1 compatibility, schema v2 evidence, and migration of pre-existing artifact rows. Exercise an object write followed by DB failure and verify the orphan is safely reconciled. Test daemon restart/lost response with both the pending and uploaded checkpoints. Record actual commands/results in `docs/evidence/08.md` and human steps in `docs/human-testing/08.md`.

## Retired: Base101 physical acceptance

The user decided on 2026-11-17 that no further physical acceptance will be
performed. The following steps are historical and must not be treated as
required work or as a blocker for milestone completion.

Do this only after the rebuilt production images and local real-process acceptance are sound:

1. Review `../missions/spin-photos/README.md`, `mission.toml`, source and its lock. Inspect exact camera name, SDK/runtime versions, dependency wheels and movement parameters. The updated requirements lock pins the runtime wheel with hash `d0e666f1358f659cb42bfadc785d0f4b064b73495fae3da5234c6139ff95df34`. The mission rotates the robot at its configured angular speed (default `0.3 rad/s`) for 21 seconds and captures six images at four-second intervals. Do not run it until the robot is in a safe clear area and an operator is ready to stop it.
2. Confirm Base101 is the intended robot in the intended organization, hosted API/Auth0 configuration is production-safe, and its daemon/Runner can reach the hosted API. Deploy the rebuilt Agent, Runner and daemon images using Base101's documented Balena workflow. Confirm those versions are running on Base101, then push the revised `spin-photos` mission so its updated lock is in the deployed revision.
3. In Console, select the correct workspace, open **Logbook**, and keep the new execution selected. Start the mission using the workflow documented by `../missions/spin-photos/README.md` after confirming the command targets Base101. While it runs, expect start/camera entries, then per-photo entries and images linked by evidence ID. Verify at least one actual camera image appears before the execution is terminal. Confirm its image opens/downloads and that its artifact metadata reports available.
4. Let the mission finish normally; confirm all expected images and the completion entry appear. Check that images are actual Base101 camera output, not the local synthetic fixture. Record execution ID, artifact IDs/count, image dimensions and hashes, mission/runtime/SDK versions, deployed image digests, timestamps, relevant logs and Console screenshots in `docs/evidence/08.md`. Do not include tokens or secrets.
5. If the run does not behave as expected, use the normal execution stop control and preserve daemon/Runner logs and queue state. Do not delete local snapshots or queue records while diagnosing a failed transfer.

Do not mark the Base101 gate passed from local mocks, a synthetic fixture image, API metadata alone, or images seen only after mission completion. The requested outcome includes seeing real photos in Console while `spin-photos` is still running.

## Handoff state

At the time this handoff was written, implementation was in progress; API
PostgreSQL tests were blocked on Testcontainers/Docker socket access and the
real-process image test had no observed final result. These are historical
status notes. See `docs/evidence/08.md` for current remaining gates. Physical
acceptance is retired. Preserve the dirty worktree.
