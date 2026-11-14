# Separate robot-side development stack

This Compose project is independent of the Ringfour API/database stack. It runs
`ringfour-agent:c-preview` and an otherwise idle ROS 2 Jazzy base container on
a private Compose network and shared ROS domain. The Agent port is bound to
localhost only. Supply a profile owned and maintained by the robot/Agent project;
this repository does not author or copy robot profiles.

```bash
export RINGFOUR_PROFILE_PATH=/absolute/path/to/agent-compatible-profile.yaml
docker compose -f infra/robot/docker-compose.yml up -d
docker compose -f infra/robot/docker-compose.yml logs agent
docker compose -f infra/robot/docker-compose.yml exec ros bash -c 'source /opt/ros/jazzy/setup.bash && ros2 topic list'
docker compose -f infra/robot/docker-compose.yml down
```

The `agent-state` volume persists across `down`; remove it only when its data can
be discarded. Set `ROBOT_ROS_DOMAIN_ID` or `RINGFOUR_AGENT_PORT` to avoid a local
conflict. This stack does not claim a functional robot, ROS graph, SDK
integration, or physical-device acceptance. An empty ROS container does not
supply robot descriptions, controllers, sensors, or onboarding. The `c-preview` image tag may move and must be rechecked before an SDK/Agent integration. Preserve existing Agent state when migrating from RoboCore; do not discard its journal or quarantine data.
Agent source describes untrusted TCP as observation-only; this development port
is not an authenticated remote control boundary.
