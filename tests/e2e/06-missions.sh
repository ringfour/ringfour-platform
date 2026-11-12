#!/usr/bin/env bash
# Isolated actual API, PostgreSQL, daemon, Runner and CLI. Uses live Auth0 login.
set -euo pipefail
cd "$(dirname "$0")/../.."
name="rf06-$$"
network="$name-net"
api="$name-api"
test_api="$name-test-api"
postgres="$name-postgres"
daemon="$name-daemon"
runner="$name-runner"
garage="$name-garage"
stage="$name-stage"
runner_data="$name-runner-data"
daemon_data="$name-daemon-data"
api_data="$name-api-data"
test_dir=$(mktemp -d "${TMPDIR:-/tmp}/ringfour-06.XXXXXXXX")
cleanup() {
  docker rm -f "$daemon" "$runner" "$test_api" "$api" "$postgres" "$garage" >/dev/null 2>&1 || true
  docker volume rm "$stage" "$runner_data" "$daemon_data" "$api_data" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
  rm -rf "$test_dir"
}
trap cleanup EXIT
set -a
. ./.auth.env
set +a
export RINGFOUR_AUTH0_DOMAIN="$AUTH0_DOMAIN" RINGFOUR_AUTH0_CLIENT_ID="$AUTH0_CLI_CLIENT_ID" RINGFOUR_AUTH0_AUDIENCE="$AUTH0_AUDIENCE"
export RINGFOUR_API_URL="http://127.0.0.1:18095"
cli=(uv run --project apps/cli ringfour-cli)
secret=$(openssl rand -hex 32)
docker build -q -t ringfour-api:06-test apps/api >/dev/null
docker build -q -t ringfour-daemon:06-test apps/daemon >/dev/null
docker build -q -t ringfour-runner:06-test apps/runner >/dev/null
docker network create "$network" >/dev/null
docker volume create "$stage" >/dev/null
docker volume create "$runner_data" >/dev/null
docker volume create "$daemon_data" >/dev/null
docker volume create "$api_data" >/dev/null
storage_args=(-e Storage__Backend=filesystem -e Storage__FilesystemRoot=/storage)
if [ "${RINGFOUR_TEST_STORAGE:-filesystem}" = garage ]; then
  rpc_secret=$(openssl rand -hex 32)
  garage_access="GK$(openssl rand -hex 16)"
  garage_secret=$(openssl rand -hex 32)
  cat >"$test_dir/garage.toml" <<EOF
metadata_dir = "/tmp/meta"
data_dir = "/tmp/data"
db_engine = "sqlite"
replication_factor = 1
rpc_bind_addr = "[::]:3901"
rpc_public_addr = "127.0.0.1:3901"
rpc_secret = "$rpc_secret"
[s3_api]
s3_region = "garage"
api_bind_addr = "[::]:3900"
root_domain = ".s3.garage.localhost"
EOF
  docker run -d --name "$garage" --network "$network" -v "$test_dir/garage.toml:/etc/garage.toml:ro" \
    -e "GARAGE_DEFAULT_ACCESS_KEY=$garage_access" -e "GARAGE_DEFAULT_SECRET_KEY=$garage_secret" \
    -e GARAGE_DEFAULT_BUCKET=ringfour-test dxflrs/garage:v2.5.0 /garage server --single-node --default-bucket >/dev/null
  for _ in $(seq 1 60); do docker exec "$garage" /garage bucket info ringfour-test >/dev/null 2>&1 && break; sleep 1; done
  docker exec "$garage" /garage bucket info ringfour-test >/dev/null 2>&1
  storage_args=(-e Storage__Backend=s3 -e "Storage__S3__Endpoint=http://$garage:3900" -e Storage__S3__Region=garage \
    -e Storage__S3__Bucket=ringfour-test -e "AWS_ACCESS_KEY_ID=$garage_access" -e "AWS_SECRET_ACCESS_KEY=$garage_secret" -e AWS_EC2_METADATA_DISABLED=true)
elif [ "${RINGFOUR_TEST_STORAGE:-filesystem}" != filesystem ]; then
  echo 'RINGFOUR_TEST_STORAGE must be filesystem or garage' >&2
  exit 2
fi
docker run -d --name "$postgres" --network "$network" -e POSTGRES_DB=ringfour -e POSTGRES_USER=ringfour -e POSTGRES_PASSWORD=temporary-test-password postgres:18 >/dev/null
for _ in $(seq 1 60); do docker exec "$postgres" pg_isready -U ringfour -d ringfour >/dev/null 2>&1 && break; sleep 1; done
docker run -d --name "$api" --network "$network" -p 127.0.0.1:18095:8080 -v "$api_data:/storage" \
  -e "ConnectionStrings__Postgres=Host=$postgres;Port=5432;Database=ringfour;Username=ringfour;Password=temporary-test-password" \
  -e "Auth0__Domain=$AUTH0_DOMAIN" -e "Auth0__Audience=$AUTH0_AUDIENCE" \
  "${storage_args[@]}" ringfour-api:06-test >/dev/null
for _ in $(seq 1 60); do curl -sf "$RINGFOUR_API_URL/health/ready" >/dev/null && break; sleep 1; done
curl -fsS "$RINGFOUR_API_URL/health/ready" >/dev/null
"${cli[@]}" whoami >/dev/null
access_token=$(uv run --project apps/cli python -c 'from ringfour_cli.auth import TokenStore; from ringfour_cli.config import load_settings; print(TokenStore().load(load_settings())["access_token"])')
api_curl() { curl -fsS -H "authorization: Bearer $access_token" "$@"; }
org=$(api_curl -H 'content-type: application/json' -d '{"name":"Milestone 05 isolated test"}' "$RINGFOUR_API_URL/v1/organizations" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
robot=$(api_curl -H 'content-type: application/json' -d '{"name":"test-robot","profile_ref":null}' "$RINGFOUR_API_URL/v1/organizations/$org/robots" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
enrollment=$(api_curl -X POST "$RINGFOUR_API_URL/v1/robots/$robot/enrollment-tokens" | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')
docker run -d --name "$runner" --network "$network" -v "$stage:/staged:ro" -v "$runner_data:/data" -e RUNNER_AUTH_TOKEN="$secret" ringfour-runner:06-test >/dev/null
[ -z "$(docker port "$runner")" ]
[ "$(docker exec "$runner" id -u)" != 0 ]
[ "$(docker inspect "$runner" --format '{{range .Mounts}}{{if eq .Destination "/staged"}}{{.RW}}{{end}}{{end}}')" = false ]
docker run -d --name "$daemon" --network "$network" -p 127.0.0.1:18096:8081 -p 127.0.0.1:18098:8083 -v "$stage:/staged" -v "$daemon_data:/data" \
  -e "RUNNER_URL=http://$runner:8082" -e RUNNER_AUTH_TOKEN="$secret" -e "PLATFORM_API_URL=http://$api:8080" ringfour-daemon:06-test >/dev/null
for service in "$api" "$daemon" "$runner"; do
  if docker inspect "$service" --format '{{range .Mounts}}{{println .Destination}}{{end}}' | grep -q docker.sock; then
    echo "$service must not mount a container-engine socket" >&2; exit 1
  fi
done
for _ in $(seq 1 60); do curl -sf 127.0.0.1:18096/health/ready >/dev/null && break; sleep 1; done
curl -fsS -H 'content-type: application/json' -d "{\"token\":\"$enrollment\"}" 127.0.0.1:18096/v1/enroll >/dev/null
for _ in $(seq 1 60); do
  online=$(api_curl "$RINGFOUR_API_URL/v1/robots/$robot/status" | python3 -c 'import json,sys; print(json.load(sys.stdin)["online"])')
  [ "$online" = True ] && break
  sleep 1
done
[ "$online" = True ] || { echo 'test daemon did not connect' >&2; exit 1; }
mkdir "$test_dir/hello"
cat >"$test_dir/hello/mission.toml" <<'EOF'
schema_version = 1
name = "hello"
entrypoint = "main.py"
python_requirement = ">=3.13,<3.14"
lock_file = "requirements.txt"
EOF
: >"$test_dir/hello/requirements.txt"
cat >"$test_dir/hello/main.py" <<'EOF'
import time
print("mission running", flush=True)
while True:
    time.sleep(1)
EOF
"${cli[@]}" "$test_dir/hello" push --org "$org" --robot "$robot" --idempotency-key smoke-push >"$test_dir/push.json"
deployment=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["deployment_id"])' "$test_dir/push.json")
revision_a=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["mission_revision_id"])' "$test_dir/push.json")
wait_deployment() {
  wanted=$1
  for _ in $(seq 1 90); do
    body=$(api_curl "$RINGFOUR_API_URL/v1/deployments/$deployment")
    state=$(python3 -c 'import json,sys; print(json.load(sys.stdin)["observed_state"])' <<<"$body")
    [ "$state" = "$wanted" ] && return 0
    sleep 1
  done
  echo "deployment expected $wanted, got $body" >&2
  docker logs --tail 50 "$api" >&2
  docker logs --tail 50 "$daemon" >&2
  docker logs --tail 50 "$runner" >&2
  return 1
}
wait_execution() {
  execution=$1 wanted=$2
  for _ in $(seq 1 90); do
    if ! body=$(api_curl "$RINGFOUR_API_URL/v1/executions/$execution" 2>/dev/null); then sleep 1; continue; fi
    state=$(python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])' <<<"$body")
    [ "$state" = "$wanted" ] && return 0
    [ "$state" != failed ] || { echo "execution failed: $body" >&2; return 1; }
    sleep 1
  done
  echo "execution expected $wanted, got $body" >&2
  return 1
}
wait_deployment ready
python3 -c 'import json,sys; x=json.load(sys.stdin); assert x["selected_revision_id"]==sys.argv[1]' "$revision_a" <<<"$body"
"${cli[@]}" mission start "$deployment" --idempotency-key smoke-start >"$test_dir/start.json"
execution_a=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["execution_id"])' "$test_dir/start.json")
wait_execution "$execution_a" running
for _ in $(seq 1 30); do
  page=$(curl -fsS http://127.0.0.1:18098/)
  if [[ "$page" == *"Mission prepared"* && "$page" == *"mission running"* && "$page" == *"Mission active"* && "$page" == *"main.py"* ]]; then break; fi
  sleep 1
done
[[ "$page" == *"Mission prepared"* && "$page" == *"mission running"* && "$page" == *"Mission active"* && "$page" == *"main.py"* ]]
echo 'PASS: daemon status page shows real mission activity, filename and Runner output'
start_op=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["operation_id"])' "$test_dir/start.json")
for _ in $(seq 1 30); do
  action_state=$(api_curl "$RINGFOUR_API_URL/v1/robots/$robot/operations/$start_op" | python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])')
  [ "$action_state" = succeeded ] && break
  sleep 1
done
[ "$action_state" = succeeded ]
python3 -c 'import json,sys; x=json.load(sys.stdin); assert x["actor_user_id"] and x["revision_id"] and x["deployment_id"]==sys.argv[1] and x["mission_revision_id"]==sys.argv[2]' "$deployment" "$revision_a" <<<"$body"
set +e
"${cli[@]}" run tests/fixtures/scripts/long.py --robot "$robot" --org "$org" --detach >"$test_dir/conflict-out" 2>&1
conflict=$?
set -e
[ "$conflict" != 0 ] && grep -q execution_conflict "$test_dir/conflict-out"
"${cli[@]}" mission restart "$deployment" --idempotency-key smoke-restart >"$test_dir/restart.json"
execution_b=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["execution_id"])' "$test_dir/restart.json")
[ "$execution_a" != "$execution_b" ]
wait_execution "$execution_a" cancelled
wait_execution "$execution_b" running
restart_op=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["operation_id"])' "$test_dir/restart.json")
for _ in $(seq 1 30); do
  action_state=$(api_curl "$RINGFOUR_API_URL/v1/robots/$robot/operations/$restart_op" | python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])')
  [ "$action_state" = succeeded ] && break
  sleep 1
done
[ "$action_state" = succeeded ]
"${cli[@]}" mission restart "$deployment" --idempotency-key smoke-restart >"$test_dir/retry.json"
cmp "$test_dir/restart.json" "$test_dir/retry.json"
docker stop "$daemon" >/dev/null
"${cli[@]}" mission stop "$deployment" --idempotency-key smoke-stop >"$test_dir/stop.json"
docker restart "$api" >/dev/null
for _ in $(seq 1 60); do curl -sf "$RINGFOUR_API_URL/health/ready" >/dev/null && break; sleep 1; done
docker start "$daemon" >/dev/null
wait_execution "$execution_b" cancelled
echo 'PASS: real publish, prepare, start, conflict, restart, idempotency and offline stop after API restart'
printf 'nonexistent-ringfour-package==1.0 --hash=sha256:%064d\n' 0 >"$test_dir/hello/requirements.txt"
"${cli[@]}" "$test_dir/hello" push --org "$org" --robot "$robot" --idempotency-key smoke-invalid >"$test_dir/bad.json"
for _ in $(seq 1 90); do
  body=$(api_curl "$RINGFOUR_API_URL/v1/deployments/$deployment")
  done_candidate=$(python3 -c 'import json,sys; x=json.load(sys.stdin); print(x["candidate_revision_id"] is None and x["last_error"] is not None)' <<<"$body")
  [ "$done_candidate" = True ] && break
  sleep 1
done
[ "$done_candidate" = True ]
python3 -c 'import json,sys; x=json.load(sys.stdin); assert x["selected_revision_id"]==sys.argv[1] and x["last_error"]["code"]=="preparation_failed"' "$revision_a" <<<"$body"
echo 'PASS: failed candidate retains selected revision'
# Interrupt a real preparation after its durable command has been accepted.
printf '\n# recovery candidate\n' >>"$test_dir/hello/main.py"
: >"$test_dir/hello/requirements.txt"
docker stop "$runner" >/dev/null
"${cli[@]}" "$test_dir/hello" push --org "$org" --robot "$robot" --idempotency-key smoke-recovery >"$test_dir/recovery.json"
revision_c=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["mission_revision_id"])' "$test_dir/recovery.json")
recovery_op=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["operation_id"])' "$test_dir/recovery.json")
for _ in $(seq 1 60); do
  prepare_state=$(docker exec "$daemon" /app/.venv/bin/python -c 'import sqlite3,sys; c=sqlite3.connect("/data/ringfour.db"); row=c.execute("SELECT state FROM device_operations WHERE operation_id=?", (sys.argv[1],)).fetchone(); print(row[0] if row else "missing")' "$recovery_op")
  [ "$prepare_state" = running ] && break
  sleep 1
done
[ "$prepare_state" = running ]
docker restart "$daemon" >/dev/null
docker start "$runner" >/dev/null
wait_deployment ready
python3 -c 'import json,sys; x=json.load(sys.stdin); assert x["selected_revision_id"]==sys.argv[1] and x["candidate_revision_id"] is None' "$revision_c" <<<"$body"
revision_a=$revision_c
echo 'PASS: daemon restart during candidate preparation resumes the same operation'
"${cli[@]}" mission start "$deployment" --idempotency-key after-failure >"$test_dir/after-failure.json"
execution_c=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["execution_id"])' "$test_dir/after-failure.json")
wait_execution "$execution_c" running
docker restart "$daemon" >/dev/null
for _ in $(seq 1 60); do
  online=$(api_curl "$RINGFOUR_API_URL/v1/robots/$robot/status" | python3 -c 'import json,sys; print(json.load(sys.stdin)["online"])')
  [ "$online" = True ] && break
  sleep 1
done
[ "$online" = True ]
wait_execution "$execution_c" running
docker restart "$runner" >/dev/null
for _ in $(seq 1 90); do
  body=$(api_curl "$RINGFOUR_API_URL/v1/executions/$execution_c")
  state=$(python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])' <<<"$body")
  [ "$state" = failed ] && break
  sleep 1
done
python3 -c 'import json,sys; x=json.load(sys.stdin); assert x["state"]=="failed" and x["error"]["code"]=="interrupted_unknown_outcome"' <<<"$body"
wait_deployment failed
python3 -c 'import json,sys; x=json.load(sys.stdin); assert x["last_execution_id"]==sys.argv[1] and x["selected_revision_id"]==sys.argv[2]' "$execution_c" "$revision_a" <<<"$body"
echo 'PASS: daemon reconnection preserves execution; Runner restart reports failure without replay'

# An immediate stop may supersede a start that has not reached the Runner yet.
"${cli[@]}" mission start "$deployment" --idempotency-key superseded-start >"$test_dir/superseded-start.json"
"${cli[@]}" mission stop "$deployment" --idempotency-key superseding-stop >"$test_dir/superseding-stop.json"
"${cli[@]}" mission stop "$deployment" --idempotency-key superseding-stop >"$test_dir/superseding-retry.json"
cmp "$test_dir/superseding-stop.json" "$test_dir/superseding-retry.json"
stop_action=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["operation_id"])' "$test_dir/superseding-stop.json")
for _ in $(seq 1 90); do
  action_state=$(api_curl "$RINGFOUR_API_URL/v1/robots/$robot/operations/$stop_action" | python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])')
  [ "$action_state" = succeeded ] && break
  [ "$action_state" != failed ] || { echo 'superseding stop failed' >&2; exit 1; }
  sleep 1
done
[ "$action_state" = succeeded ]
active=$(docker exec "$postgres" psql -At -U ringfour -d ringfour -c "SELECT count(*) FROM script_executions WHERE robot_id='$robot' AND state IN ('queued','preparing','running')")
[ "$active" = 0 ]
echo 'PASS: immediate stop supersedes start and its idempotent replay preserves the action'
