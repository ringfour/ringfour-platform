# Current combined acceptance

Use [AP-14/AP-15 combined acceptance](AP-14-15.md) for the current two-user sweep.
The early sequence below is historical context; its old overlay/Runner references are retired.

# Early acceptance — AP-05, AP-06, AP-08 and AP-07

**Current sequence:** AP-08/09/10 implementation and automated protocol checks
are complete. AP-08V is the next implementation slice by user direction. Then
run the [combined AP-08/09/10/AP-08V human pass](AP-08-10.md), including the
[virtual topology checks](AP-08V.md). These early publication/install checks
remain useful.

Run one flow in dependency order: configure/deploy → publish → discover/import →
install → configure → update/rollback → standalone live IMU. AP-01 through AP-04
are already accepted. Hosted start/stop and runtime delivery are now implemented in AP-09/AP-10. Installation success creates no App run/container.

Earlier deployment inspection found App dispatch disabled and the Daemon using
Runner readiness. Local registry configuration/publication subsequently passed.
Enable the App flag and Daemon overlay for the combined deployment acceptance.

## 1. Configure registry policy, then deploy both consumers

The user selected the local official registry plus Joxit UI. The existing ignored
Platform `.env` is prepared for it. Start with the
[local registry guide](local-registry.md):

```sh
cd /home/cdr/ringfour/ringfour-platform
./up.sh -d app-registry registry-ui api
```

The registry is `127.0.0.1:5000`; browse it at http://localhost:5080.
Publish under `127.0.0.1:5000/ringfour/...`. Joxit uses its same-origin proxy,
and the API uses the development-only internal endpoint mapping. The local
registry has anonymous local pulls; this does not accept a private hosted
provider's token policy. Hosted policies still require HTTPS.

Run the Daemon overlay/socket-GID commands and API launch in
[AP-07 §1](AP-07.md#1-deploy-both-consumers-operator-only), preserving the existing
Compose project, enrollment, data volume, network and aliases. The current
singleton is `ringfour-virtual` on `base101_default`. The operator performs these
stack actions under the workspace AGENTS.md.

Verify:

```sh
curl --fail-with-body -sS http://localhost:8080/health/ready
curl --fail-with-body -sS http://localhost:8081/health/ready
```

Pass: API readiness is healthy; Daemon readiness contains `sqlite: ok` and
`engine: ok`. Its robot keeps the same UUID/namespace and reports a fresh
Agent inventory/actual engine target through `/v1/robots/ROBOT_UUID/app-status`.

## 2. Publish Robot Check through the real CLI

From Platform, identify the existing publisher workspace and robot:

```sh
uv run --project apps/cli ringfour-cli whoami
uv run --project apps/cli ringfour-cli org list
uv run --project apps/cli ringfour-cli robot list
```

Use `ringfour-cli login` if the normal Auth0 session needs refreshing. Select a
Developer/Owner publishing workspace, and initialize a new project destination:

```sh
uv run --project apps/cli ringfour-cli org select PUBLISHER_ORGANIZATION_UUID
uv run --project apps/cli ringfour-cli app init /tmp/robot-check-acceptance \
  --app-key robot-check-acceptance --repository 127.0.0.1:5000/ringfour/robot-check
uv run --project apps/cli ringfour-cli app validate /tmp/robot-check-acceptance --json
uv run --project apps/cli ringfour-cli app build /tmp/robot-check-acceptance --json
uv run --project apps/cli ringfour-cli app publish /tmp/robot-check-acceptance --json
```

Pass: publication returns App/revision IDs and publisher provenance; final
`app.manifest.json` references a concrete `@sha256:` image. Save that exact image
as `IMAGE`, and the revision UUID for installation. Repeating publish returns
the same revision. [AP-06](AP-06.md) covers invalid inputs/frozen-release checks.

## 3. Check real workspace/actor boundaries

Using A's and B's own Auth0 tokens and separate workspaces, follow
[AP-05 §2–3](AP-05.md#2-publish-and-discover-a-real-release):

- B discovers the listed App and imports its revision; repeat import retains its ID.
- B cannot publish into A; A cannot read B's private library/robot resources.
- Publish a separate unlisted release. B needs its exact destination-bound share.
- Revoking the share prevents new imports through it; existing imports remain.
- An Operator can inspect but cannot perform Developer/Owner mutations.

Use the existing robot's owner workspace for the initial install check. An
end-to-end A-publishes/B-installs check requires a robot already owned by B;
metadata sharing alone does not change robot ownership.

## 4. Accept installation/configuration/update/rollback

Follow [AP-07 §2–5](AP-07.md#2-install-a-published-release) with a real Auth0 token,
the existing robot UUID and the published revision UUID. Save every operation,
installation, revision and configuration ID.

| Check | Passing outcome |
|---|---|
| Install with `sample_count: 1` | 202 with `run_id: null`, then operation `succeeded/applied`, installed binding present and candidate cleared. |
| Repeat identical request/key | Same installation/operation IDs. |
| Change input under the same key | `idempotency_conflict`. |
| Configure count 2 with current expected config ID | New immutable config ID; previous config remains readable; no new image pull/run/container. |
| Count 0/21, boolean, extra field, or stale expected ID | Structured refusal; installed binding unchanged. |
| Publish v2, then update with matching expected IDs/config | New installed revision/config after observed application; v1 metadata remains unchanged. |
| Preparation fails | Failed candidate visible; previous installed binding retained. |
| Roll back to an applied v1 revision/config pair | Old selection restored while idle, without executing it. |
| Install a second distinct App | Two independent installation IDs on the robot. |

Inspect owned workload containers with the documented label filter:

```sh
docker ps -a --filter "label=eu.ringfour.robot-id=$ROBOT"
```

For this install-only sequence, expect no new App workload container or run.
The [AP-08 engine harness](AP-08.md#independent-verification-without-reloading-stacks)
already covers actual create/start/stop recovery in its isolated fixture; those
hosted user commands arrive in AP-09.

## 5. Accept live IMU separately

Start Base101 through its normal operator workflow so the Agent receives
advancing IMU data. For the inspected singleton network/Agent alias, run the
exact published image as a standalone read-only SDK check:

```sh
docker run --rm --network base101_default \
  -e RINGFOUR_AGENT_URL=ws://agent:10101 "$IMAGE"
```

Pass: one `robot_check.imu` JSON record with an advancing Agent stamp followed by
`robot_check.complete`, and exit 0. A timeout fails sensor acceptance even when
inventory advertises IMU. This standalone check does not produce a hosted App run;
hosted start/stop and runtime Logbook acceptance remain AP-09/AP-10.

## 6. Record acceptance

Record the image digest, publisher/consumer/robot UUIDs, installation/operation/
configuration IDs, observed results, denials and live IMU result. Do not include
tokens. Distinguish completed live checks from the separate private-provider,
multi-robot topology (AP-08V) and future hosted execution gates.

The component suites and all four isolated protocol harnesses have passed during
implementation. To repeat the protocol checks independently of the live stacks:

```sh
uv run --project apps/daemon python tests/e2e/ap05-catalog.py
uv run --project apps/daemon python tests/e2e/ap06-publish.py
uv run --project apps/daemon python tests/e2e/ap08-engine.py
uv run --project apps/daemon python tests/e2e/ap07-install.py
```

They operate disposable test infrastructure and clean up their own stores.
They do not substitute for real Auth0/provider/deployed sensor evidence.
