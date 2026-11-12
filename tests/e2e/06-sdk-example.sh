#!/usr/bin/env bash
# Real Runner and Agent with the Agent project's synthetic profile; no physical robot.
set -euo pipefail
cd "$(dirname "$0")/../.."
name="rf-sdk-$$"
network="$name-net"
agent="$name-agent"
runner="$name-runner"
agent_volume="$name-socket"
test_dir=$(mktemp -d "${TMPDIR:-/tmp}/ringfour-sdk.XXXXXXXX")
cleanup() {
  docker rm -f "$runner" "$agent" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
  docker volume rm "$agent_volume" >/dev/null 2>&1 || true
  rm -rf "$test_dir"
}
trap cleanup EXIT
profile="${ROBOCORE_TEST_PROFILE:-$HOME/Work/robocore-sdk/artifacts/robocore-c-preview/source/robocore-sdk/tests/fixtures/profiles/base.yaml}"
[ -f "$profile" ]
mkdir -p "$test_dir/staged/mission"
cp ../missions/capabilities/main.py ../missions/capabilities/requirements.lock "$test_dir/staged/mission/"
chmod -R a+rX "$test_dir"
uv run --project apps/cli python - "$test_dir" <<'PY'
import json, pathlib, sys
from ringfour_cli.mission_bundle import build_mission_bundle
folder = pathlib.Path(sys.argv[1])
mission = build_mission_bundle(pathlib.Path('../missions/capabilities'))
bundle = mission.bundle
(folder / 'prepare.json').write_text(json.dumps({'revision_digest': bundle.revision_digest, 'code_ref': 'mission',
    'entrypoint': bundle.entrypoint, 'python_requirement': mission.manifest['python_requirement'],
    'lock_sha256': bundle.lock_sha256, 'retain_digests': []}))
(folder / 'start.json').write_text(json.dumps({'execution_id': '7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e',
    'robot_id': '9b8a7c6d-5e4f-4a3b-8c7d-6e5f4a3b2c1d', 'revision_digest': bundle.revision_digest,
    'code_ref': 'mission', 'entrypoint': bundle.entrypoint, 'python_requirement': mission.manifest['python_requirement'],
    'lock_sha256': bundle.lock_sha256}))
PY
docker build -q -t ringfour-runner:sdk-test apps/runner >/dev/null
docker network create "$network" >/dev/null
if [ "${ROBOCORE_TEST_SOCKET:-0}" = 1 ]; then
  robot_root="${BASE101_ROOT:-$HOME/Work/robots/base101}"
  docker build -q -f "$robot_root/docker/Dockerfile.agent" -t base101-agent:socket-test "$robot_root" >/dev/null
  docker volume create "$agent_volume" >/dev/null
  # Fast DDS avoids needing the robot's Zenoh router for this synthetic profile.
  docker run -d --name "$agent" --network "$network" -e RMW_IMPLEMENTATION=rmw_fastrtps_cpp \
    -v "$profile:/etc/robocore/profile.yaml:ro" -v "$agent_volume:/run/robocore" \
    base101-agent:socket-test agent --profile /etc/robocore/profile.yaml --socket /run/robocore/agent.sock \
    --host 127.0.0.1 --port 10101 --state-dir /var/lib/robocore >/dev/null
  docker run -d --name "$runner" --network "$network" -p 127.0.0.1:18112:8082 \
    -v "$test_dir/staged:/staged:ro" -v "$agent_volume:/run/robocore:ro" \
    -e RUNNER_AUTH_TOKEN=local-test-secret -e ROBOCORE_AGENT_URL=unix:///run/robocore/agent.sock ringfour-runner:sdk-test >/dev/null
else
  docker run -d --name "$agent" --network "$network" --network-alias agent -v "$profile:/etc/robocore/profile.yaml:ro" robocore-agent:c-preview >/dev/null
  docker run -d --name "$runner" --network "$network" -p 127.0.0.1:18112:8082 -v "$test_dir/staged:/staged:ro" \
    -e RUNNER_AUTH_TOKEN=local-test-secret -e ROBOCORE_AGENT_URL=ws://agent:10101 ringfour-runner:sdk-test >/dev/null
fi
for _ in $(seq 1 60); do
  docker logs "$agent" 2>&1 | grep -q 'agent ready' && break
  sleep 1
done
docker logs "$agent" 2>&1 | grep -q 'agent ready'
if [ "${ROBOCORE_TEST_SOCKET:-0}" = 1 ]; then
  # A killed Agent must reclaim the socket left in its persistent volume.
  docker kill "$agent" >/dev/null
  docker start "$agent" >/dev/null
  for _ in $(seq 1 60); do
    docker exec "$agent" python3 -c 'import socket; s=socket.socket(socket.AF_UNIX); s.settimeout(1); s.connect("/run/robocore/agent.sock"); s.close()' >/dev/null 2>&1 && break
    sleep 1
  done
  docker exec "$agent" python3 -c 'import socket; s=socket.socket(socket.AF_UNIX); s.settimeout(1); s.connect("/run/robocore/agent.sock"); s.close()'
fi
for _ in $(seq 1 60); do curl -sf http://127.0.0.1:18112/health/ready >/dev/null && break; sleep 1; done
curl -fsS --max-time 300 -H 'Authorization: Bearer local-test-secret' -H 'content-type: application/json' \
  --data-binary @"$test_dir/prepare.json" http://127.0.0.1:18112/v1/revisions/prepare >"$test_dir/prepared.json"
python3 -c 'import json,sys; assert json.load(open(sys.argv[1]))["state"]=="ready"' "$test_dir/prepared.json"
curl -fsS -H 'Authorization: Bearer local-test-secret' -H 'content-type: application/json' \
  --data-binary @"$test_dir/start.json" http://127.0.0.1:18112/v1/executions > /dev/null
execution=7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e
for _ in $(seq 1 60); do
  state=$(curl -fsS -H 'Authorization: Bearer local-test-secret' "http://127.0.0.1:18112/v1/executions/$execution" | python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])')
  [ "$state" = succeeded ] && break
  [ "$state" != failed ] || break
  sleep 1
done
curl -fsS -H 'Authorization: Bearer local-test-secret' "http://127.0.0.1:18112/v1/executions/$execution/output?stream=stdout&offset=0&limit=16384" | python3 -c 'import base64,json,sys; print(base64.b64decode(json.load(sys.stdin)["data_base64"]).decode())' >"$test_dir/stdout"
if [ "$state" != succeeded ]; then
  curl -fsS -H 'Authorization: Bearer local-test-secret' "http://127.0.0.1:18112/v1/executions/$execution/output?stream=stderr&offset=0&limit=16384" | python3 -c 'import base64,json,sys; print(base64.b64decode(json.load(sys.stdin)["data_base64"]).decode())' >&2
  echo "SDK mission ended $state" >&2
  exit 1
fi
grep -q '^Robot:' "$test_dir/stdout"
grep -q '^Capabilities:' "$test_dir/stdout"
cat "$test_dir/stdout"
echo 'PASS: pinned RoboCore SDK mission used the actual Runner and synthetic-profile Agent'
