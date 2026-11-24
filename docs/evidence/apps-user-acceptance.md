# User acceptance follow-up — 24 November 2026

User-provided terminal evidence confirms real Auth0 access for local user
`b7278210-3edb-484a-83d4-2191b5cb53ec`, owner membership in kraftwerk
`88d119d4-8f51-42dc-b1fd-65376f6b13b3` and HITL acceptance isolation
`c3f97caf-ee44-43e9-a1c8-8c9b8b69d84f`. Existing enrolled robot virt2 is
`91bc543a-db5f-4b90-bb58-5b7abe17a4cd`.

The user initialized/validated/built the pinned Robot Check v0.1.1 template and
published release 0.1.0 through the real Auth0 CLI/local registry/API:

- App `8872028a-7529-40fc-b7c1-b401b3cfd736`
- Revision `cb9fb22b-6d72-4264-94a2-2cb3cb3516eb`
- Image `127.0.0.1:5000/ringfour/robot-check@sha256:a0c3cfd06094959bf09be1d3ef1f10d9af6abc90d34f6f58453459fb0d9235dd`
- Manifest digest `sha256:c5f6a8d24dedb177e9c0246ade6000efdcea55db4e12852a2417c97db5f358d1`
- Published at `2026-11-24T08:06:01.400576Z`, listed, original kraftwerk publisher.

The user's transcripts also report passes for all four isolated harnesses:
AP-05 catalog/private scoped grants/refresh/replay; AP-06 clean publication,
fresh pull and immutable v1/v2; AP-08 real engine/replacement/output recovery;
AP-07 installation/configuration/update/rollback and API/Daemon replacement.
These are user-reported reruns, not additional agent reruns. The legacy-builder
message belongs to the AP-08 test fixture; that fixture reported passed.
Ownership of two workspaces by one identity does not prove separate real Auth0
principal isolation. Live virt2 installation and live sensor/run acceptance are
not inferred from those isolated harnesses.

## Requested standalone live IMU demonstration

The user explicitly requested seeing the IMU App running. The agent started the
exact published image with its existing plain Python entrypoint and uid/gid
10001:10001 on the inspected `base101_default` network, connecting to
`ws://agent:10101`. A root-owned mode-0444 configuration copied before start
requested 20 samples. The container received no engine socket, privileges,
human/registry/runtime credentials, ports or command override; capabilities were
dropped and CPU/memory/PID limits set. This is a standalone operator check, not
an AP-09 hosted run or fabricated installation/execution row.

Container: `ringfour-imu-demo-9462c100`, retained for user inspection/retry.
Observed interval: `2026-11-24T08:11:41Z` to `2026-11-24T08:11:52Z`.
Result: `robot_check.failed` / `TimeoutError`, exit 1; no IMU sample emitted.
Metadata/log files: `/tmp/ringfour-imu-demo.json`, `/tmp/ringfour-imu-demo.log`.

A bounded read-only ROS query inside the running Agent confirmed
`/sensors/imu` is `sensor_msgs/msg/Imu`, **publisher count 0**, subscription count
1 (`ringfour_agent`). No simulator container appeared in the running-container
list. IMU advertisement alone is not sensor readiness; no positive live IMU
acceptance is claimed. The operator must start Base101 through the existing
virtual README workflow. No stack reload/rebuild/recreate/restart was performed.
The dirty Base101 submodule was not changed.

After the operator starts Base101, rerun the same standalone image/config with:

```sh
docker start --attach ringfour-imu-demo-9462c100
```

Expect twenty timestamped `robot_check.imu` records with advancing Agent stamps,
then `robot_check.complete` and exit 0. Hosted start/stop/runtime evidence remain
AP-09/AP-10 work.

## Successful live IMU acceptance

After the operator started the simulator, the user reran the same published
image/container and supplied the complete 20-sample output. Read-only container
inspection independently confirms exit 0 and the latest interval
`2026-11-24T08:22:51.758452707Z` to `2026-11-24T08:22:54.779397723Z`.
The retained final 21 log records were parsed and checked: samples numbered
1–20, finite acceleration values, strictly advancing Agent timestamps from
`208.081000001` to `210.001000001`, frame `imu_link`, and final
`robot_check.complete` with `samples: 20`. Successful logs are retained at
`/tmp/ringfour-imu-demo-success.log`.

This closes **AP-06 live standalone Robot Check acceptance for the selected
local-registry preview**. Earlier timeout evidence above is historical and its
missing-publisher prerequisite is resolved. Publication through real Auth0/local
registry plus all four user-reported isolated harness passes are recorded above.
This is a standalone run, not a hosted App lifecycle claim.

Remaining deployment checks were inspected separately: live API
`Apps__InstallationsEnabled=false`, Daemon `RINGFOUR_APPS_ENABLED` absent, and
Daemon readiness still checks SQLite/Runner. Therefore real virt2 install/
configure/update/rollback and AP-08 socket/GID deployment acceptance remain
pending. Separate real Auth0-principal isolation is also pending; owning two
workspaces with one identity does not establish it. Hosted start/stop/runtime
remain AP-09/AP-10 and multi-robot topology remains AP-08V.
