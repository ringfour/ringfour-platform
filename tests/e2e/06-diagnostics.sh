#!/usr/bin/env bash
# Synthetic ROS sensor publishers -> actual Agent -> Unix socket -> Runner mission.
set -euo pipefail
cd "$(dirname "$0")/../.."
name="rf-diag-$$"
network="$name-net"
agent="$name-agent"
runner="$name-runner"
volume="$name-socket"
test_dir=$(mktemp -d "${TMPDIR:-/tmp}/ringfour-diagnostics.XXXXXXXX")
cleanup() {
  docker rm -f "$runner" "$agent" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
  docker volume rm "$volume" >/dev/null 2>&1 || true
  rm -rf "$test_dir"
}
trap cleanup EXIT
profile="${ROBOCORE_TEST_PROFILE:-$HOME/Work/robocore-sdk/artifacts/robocore-c-preview/source/robocore-sdk/tests/fixtures/profiles/base.yaml}"
robot_root="${BASE101_ROOT:-$HOME/Work/robots/base101}"
[ -f "$profile" ]
mkdir -p "$test_dir/staged/mission"
cp ../missions/diagnostics/main.py ../missions/diagnostics/requirements.lock "$test_dir/staged/mission/"
cat > "$test_dir/publish.py" <<'PY'
import time
import rclpy
from sensor_msgs.msg import Image, Imu

rclpy.init()
node = rclpy.create_node('ringfour_synthetic_sensors')
imu_pub = node.create_publisher(Imu, '/fixture/imu', 10)
image_pub = node.create_publisher(Image, '/base_camera/image_raw', 10)
try:
    while rclpy.ok():
        stamp = node.get_clock().now().to_msg()
        imu = Imu()
        imu.header.stamp = stamp
        imu.header.frame_id = 'fixture_imu'
        imu.orientation.w = 1.0
        imu.linear_acceleration.z = 9.81
        imu.angular_velocity.z = 0.25
        imu_pub.publish(imu)
        image = Image()
        image.header.stamp = stamp
        image.header.frame_id = 'camera_optical_frame'
        image.height, image.width, image.encoding, image.step = 2, 2, 'rgb8', 6
        image.data = [255, 0, 0] * 4
        image_pub.publish(image)
        rclpy.spin_once(node, timeout_sec=0)
        time.sleep(0.1)
finally:
    node.destroy_node()
    rclpy.shutdown()
PY
chmod -R a+rX "$test_dir"
uv run --project apps/cli python - "$test_dir" <<'PY'
import json, pathlib, sys
from ringfour_cli.mission_bundle import build_mission_bundle
folder = pathlib.Path(sys.argv[1])
mission = build_mission_bundle(pathlib.Path('../missions/diagnostics'))
bundle = mission.bundle
common = {'revision_digest': bundle.revision_digest, 'code_ref': 'mission',
          'entrypoint': bundle.entrypoint, 'python_requirement': mission.manifest['python_requirement'],
          'lock_sha256': bundle.lock_sha256}
(folder / 'prepare.json').write_text(json.dumps({**common, 'retain_digests': []}))
(folder / 'start.json').write_text(json.dumps({**common, 'execution_id': '7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e',
    'robot_id': '9b8a7c6d-5e4f-4a3b-8c7d-6e5f4a3b2c1d'}))
PY
docker build -q -t ringfour-runner:diagnostics-test apps/runner >/dev/null
docker build -q -f "$robot_root/docker/Dockerfile.agent" -t base101-agent:socket-test "$robot_root" >/dev/null
docker network create "$network" >/dev/null
docker volume create "$volume" >/dev/null
docker run -d --name "$agent" --network "$network" -e RMW_IMPLEMENTATION=rmw_fastrtps_cpp \
  -v "$profile:/etc/robocore/profile.yaml:ro" -v "$volume:/run/robocore" -v "$test_dir:/fixture:ro" \
  base101-agent:socket-test agent --profile /etc/robocore/profile.yaml --socket /run/robocore/agent.sock \
  --host 127.0.0.1 --port 10101 --state-dir /var/lib/robocore >/dev/null
docker run -d --name "$runner" --network "$network" -p 127.0.0.1:18113:8082 \
  -v "$test_dir/staged:/staged:ro" -v "$volume:/run/robocore:ro" \
  -e RUNNER_AUTH_TOKEN=local-test-secret -e ROBOCORE_AGENT_URL=unix:///run/robocore/agent.sock ringfour-runner:diagnostics-test >/dev/null
for _ in $(seq 1 60); do docker logs "$agent" 2>&1 | grep -q 'agent ready' && break; sleep 1; done
docker logs "$agent" 2>&1 | grep -q 'agent ready'
if [ "${ROBOCORE_TEST_NO_SENSORS:-0}" != 1 ]; then
  docker exec -d "$agent" bash -c 'source /opt/ros/jazzy/setup.bash && python3 /fixture/publish.py'
fi
for _ in $(seq 1 60); do curl -sf http://127.0.0.1:18113/health/ready >/dev/null && break; sleep 1; done
curl -fsS --max-time 300 -H 'Authorization: Bearer local-test-secret' -H 'content-type: application/json' \
  --data-binary @"$test_dir/prepare.json" http://127.0.0.1:18113/v1/revisions/prepare >/dev/null
curl -fsS -H 'Authorization: Bearer local-test-secret' -H 'content-type: application/json' \
  --data-binary @"$test_dir/start.json" http://127.0.0.1:18113/v1/executions >/dev/null
execution=7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e
for _ in $(seq 1 45); do
  curl -fsS -H 'Authorization: Bearer local-test-secret' "http://127.0.0.1:18113/v1/executions/$execution/output?stream=stdout&offset=0&limit=16384" | \
    python3 -c 'import base64,json,sys; sys.stdout.buffer.write(base64.b64decode(json.load(sys.stdin)["data_base64"]))' >"$test_dir/output"
  if [ "${ROBOCORE_TEST_NO_SENSORS:-0}" = 1 ]; then
    if grep -q 'IMU observed: unavailable' "$test_dir/output" && grep -q 'camera base delivered: waiting for data' "$test_dir/output"; then break; fi
  elif grep -Eq 'IMU observed: [1-9][0-9.]* Hz' "$test_dir/output" && grep -Eq 'camera base delivered: [1-9][0-9.]* fps' "$test_dir/output"; then break; fi
  state=$(curl -fsS -H 'Authorization: Bearer local-test-secret' "http://127.0.0.1:18113/v1/executions/$execution" | python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])')
  [ "$state" != failed ] || break
  sleep 1
done
cat "$test_dir/output"
if [ "${ROBOCORE_TEST_NO_SENSORS:-0}" = 1 ]; then
  imu_pattern='IMU observed: unavailable'
  camera_pattern='camera base delivered: waiting for data'
else
  imu_pattern='IMU observed: [1-9][0-9.]* Hz'
  camera_pattern='camera base delivered: [1-9][0-9.]* fps'
fi
if ! grep -Eq "$imu_pattern" "$test_dir/output" || ! grep -Eq "$camera_pattern" "$test_dir/output"; then
  curl -fsS -H 'Authorization: Bearer local-test-secret' "http://127.0.0.1:18113/v1/executions/$execution/output?stream=stderr&offset=0&limit=16384" | python3 -c 'import base64,json,sys; sys.stderr.buffer.write(base64.b64decode(json.load(sys.stdin)["data_base64"]))'
  echo 'Diagnostics did not receive both synthetic sensors.' >&2
  exit 1
fi
curl -fsS -X POST -H 'Authorization: Bearer local-test-secret' "http://127.0.0.1:18113/v1/executions/$execution/stop" >/dev/null
for _ in $(seq 1 30); do
  state=$(curl -fsS -H 'Authorization: Bearer local-test-secret' "http://127.0.0.1:18113/v1/executions/$execution" | python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])')
  [ "$state" = cancelled ] && break
  sleep 1
done
[ "$state" = cancelled ]
echo "PASS: diagnostics through Agent, Unix socket, SDK and Runner (no sensors: ${ROBOCORE_TEST_NO_SENSORS:-0}); stop cancels mission"
