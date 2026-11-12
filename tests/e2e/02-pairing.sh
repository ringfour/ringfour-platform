#!/usr/bin/env bash
# Actual API + daemon + CLI; creates an isolated daemon volume and one test robot.
set -euo pipefail
cd "$(dirname "$0")/../.."
name="ringfour-pairing-test-$$"
volume="$name-data"
cleanup() {
  local result=$?
  docker rm -f "$name" >/dev/null 2>&1 || true
  docker volume rm "$volume" >/dev/null 2>&1 || true
  if [[ ${robot:-} =~ ^[0-9a-f-]{36}$ ]]; then
    # Delete only the robot created by this test and its dependent records.
    if ! docker exec -i ringfour-postgres-1 psql -q -v ON_ERROR_STOP=1 \
      -v robot="$robot" -v expected_name="$name" -U ringfour -d ringfour >/dev/null <<'SQL'
BEGIN;
CREATE TEMP TABLE test_robot ON COMMIT DROP AS
SELECT id FROM robots WHERE id = :'robot'::uuid AND name = :'expected_name';
DELETE FROM operation_idempotency_records WHERE outcome_operation_id IN (SELECT id FROM operations WHERE robot_id IN (SELECT id FROM test_robot));
DELETE FROM operations WHERE robot_id IN (SELECT id FROM test_robot);
DELETE FROM robot_presence WHERE robot_id IN (SELECT id FROM test_robot);
DELETE FROM device_pairing_requests WHERE robot_id IN (SELECT id FROM test_robot);
DELETE FROM robot_audit_events WHERE robot_id IN (SELECT id FROM test_robot);
DELETE FROM enrollment_tokens WHERE robot_id IN (SELECT id FROM test_robot);
DELETE FROM robot_credentials WHERE robot_id IN (SELECT id FROM test_robot);
DELETE FROM robots WHERE id IN (SELECT id FROM test_robot);
COMMIT;
SQL
    then
      echo "Failed to remove test robot $robot" >&2
      result=1
    fi
  fi
  trap - EXIT
  exit "$result"
}
trap cleanup EXIT
set -a
. ./.auth.env
set +a
export RINGFOUR_AUTH0_DOMAIN="$AUTH0_DOMAIN" RINGFOUR_AUTH0_CLIENT_ID="$AUTH0_CLI_CLIENT_ID" RINGFOUR_AUTH0_AUDIENCE="$AUTH0_AUDIENCE"
cli=(uv run --project apps/cli ringfour-cli)
"${cli[@]}" whoami >/dev/null
org=$(uv run --project apps/cli python -c 'from ringfour_cli.config import load_settings,selected_org; print(selected_org(load_settings()) or "")')
[ -n "$org" ] || { echo 'Select an owner organization first.' >&2; exit 1; }
curl -fsS localhost:8080/health/ready >/dev/null
# Verify the running Console serves the pairing UI, then exercise its real
# proxy with the same authenticated claim request the browser sends.
curl -fsS localhost:5173/src/components/RobotsPage.tsx | grep 'pairing-claims' >/dev/null
docker volume create "$volume" >/dev/null
docker run -d --name "$name" --network ringfour_default -p 127.0.0.1:18083:8083 -p 127.0.0.1:18084:8081 \
  -v "$volume:/data" -e PLATFORM_API_URL=http://api:8080 -e RUNNER_URL=http://runner:8082 \
  ringfour-daemon:latest >/dev/null
for _ in $(seq 1 60); do
  page=$(curl -sf localhost:18083/ || true)
  [[ "$page" == *"Ready to pair"* ]] && break
  sleep 1
done
[[ "$page" == *"Ready to pair"* ]] || { echo 'Device page did not display pairing code' >&2; exit 1; }
code=$(python3 -c 'import re,sys; x=re.search(r"[A-Z2-7]{4}(?:-[A-Z2-7]{4}){5}-[A-Z2-7]{2}",sys.stdin.read()); print(x.group(0) if x else "")' <<<"$page")
[ -n "$code" ]
[ "$(curl -s -o /dev/null -w '%{http_code}' -X POST localhost:18083/v1/enroll)" = 404 ]
# Pending secrets and the code survive a daemon restart.
docker restart "$name" >/dev/null
for _ in $(seq 1 30); do
  page=$(curl -sf localhost:18083/ || true)
  [[ "$page" == *"$code"* ]] && break
  sleep 1
done
[[ "$page" == *"$code"* ]] || { echo 'Pairing code changed after restart' >&2; exit 1; }
access_token=$(uv run --project apps/cli python -c 'from ringfour_cli.auth import TokenStore; from ringfour_cli.config import load_settings; print(TokenStore().load(load_settings())["access_token"])')
claim=$(curl -fsS -H "authorization: Bearer $access_token" -H 'content-type: application/json' \
  -d "{\"code\":\"$code\",\"name\":\"$name\"}" "localhost:5173/api/v1/organizations/$org/pairing-claims")
robot=$(python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])' <<<"$claim")
[ -n "$robot" ]
for _ in $(seq 1 30); do
  identity=$(curl -sf localhost:18084/v1/identity || true)
  [[ "$identity" == *"$robot"* ]] && break
  sleep 1
done
[[ "$identity" == *"$robot"* ]]
for _ in $(seq 1 30); do
  page=$(curl -sf localhost:18083/ || true)
  [[ "$page" == *"Enrolled · connected"* ]] && break
  sleep 1
done
[[ "$page" == *"Enrolled · connected"* ]]
[[ "$page" != *"$code"* ]]
docker restart "$name" >/dev/null
for _ in $(seq 1 30); do
  identity=$(curl -sf localhost:18084/v1/identity || true)
  [[ "$identity" == *"$robot"* ]] && break
  sleep 1
done
[[ "$identity" == *"$robot"* ]]
echo "PASS: pairing page, code persistence, Console proxy claim, connection and restart recovery; cleaning up test robot"
