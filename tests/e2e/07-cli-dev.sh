#!/usr/bin/env bash
# Isolated actual Platform, PostgreSQL, daemon and local TLS listener.
# Actual CLI development commands over isolated Platform and LAN services.
set -euo pipefail
cd "$(dirname "$0")/../.."
name="rf07-grant-$$"
network="$name-net"
api="$name-api"
postgres="$name-postgres"
daemon="$name-daemon"
runner="$name-runner"
stage="$name-stage"
runner_data="$name-runner-data"
daemon_data="$name-daemon-data"
api_data="$name-api-data"
cleanup() {
  docker rm -f "$daemon" "$runner" "$api" "$postgres" >/dev/null 2>&1 || true
  docker volume rm "$stage" "$runner_data" "$daemon_data" "$api_data" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
  if [ -n "${test_dir:-}" ]; then rm -rf "$test_dir"; fi
}
trap cleanup EXIT
set -a
. ./.auth.env
set +a
export RINGFOUR_AUTH0_DOMAIN="$AUTH0_DOMAIN" RINGFOUR_AUTH0_CLIENT_ID="$AUTH0_CLI_CLIENT_ID" RINGFOUR_AUTH0_AUDIENCE="$AUTH0_AUDIENCE"
export RINGFOUR_API_URL="http://127.0.0.1:18095"
cli=(uv run --project "$PWD/apps/cli" ringfour-cli)
secret=$(openssl rand -hex 32)
docker build -q -t ringfour-api:07-test apps/api >/dev/null
docker build -q -t ringfour-daemon:07-test apps/daemon >/dev/null
docker build -q -t ringfour-runner:07-test apps/runner >/dev/null
docker network create "$network" >/dev/null
for v in "$stage" "$runner_data" "$daemon_data" "$api_data"; do docker volume create "$v" >/dev/null; done
docker run -d --name "$postgres" --network "$network" -e POSTGRES_DB=ringfour -e POSTGRES_USER=ringfour -e POSTGRES_PASSWORD=temporary-test-password postgres:18 >/dev/null
for _ in $(seq 1 60); do docker exec "$postgres" pg_isready -U ringfour -d ringfour >/dev/null 2>&1 && break; sleep 1; done
docker run -d --name "$api" --network "$network" -p 127.0.0.1:18095:8080 -v "$api_data:/storage" \
  -e "ConnectionStrings__Postgres=Host=$postgres;Port=5432;Database=ringfour;Username=ringfour;Password=temporary-test-password" \
  -e "Auth0__Domain=$AUTH0_DOMAIN" -e "Auth0__Audience=$AUTH0_AUDIENCE" -e Storage__Backend=filesystem -e Storage__FilesystemRoot=/storage ringfour-api:07-test >/dev/null
for _ in $(seq 1 60); do curl -sf "$RINGFOUR_API_URL/health/ready" >/dev/null && break; sleep 1; done
curl -fsS "$RINGFOUR_API_URL/health/ready" >/dev/null
"${cli[@]}" whoami >/dev/null
access_token=$(uv run --project apps/cli python -c 'from ringfour_cli.auth import TokenStore; from ringfour_cli.config import load_settings; print(TokenStore().load(load_settings())["access_token"])')
api_curl() { curl -fsS -H "authorization: Bearer $access_token" "$@"; }
test_dir=$(mktemp -d "${TMPDIR:-/tmp}/ringfour-cli-dev.XXXXXXXX")
mkdir "$test_dir/wheels"
python3 - "$test_dir/wheels" <<'PY'
import pathlib, sys, zipfile
path = pathlib.Path(sys.argv[1]) / 'ringfour_test_dep-1.0-py3-none-any.whl'
with zipfile.ZipFile(path, 'w') as wheel:
    wheel.writestr('ringfour_test_dep.py', 'VALUE = "dependency installed"\n')
    wheel.writestr('ringfour_test_dep-1.0.dist-info/METADATA', 'Metadata-Version: 2.1\nName: ringfour-test-dep\nVersion: 1.0\n')
    wheel.writestr('ringfour_test_dep-1.0.dist-info/WHEEL', 'Wheel-Version: 1.0\nGenerator: ringfour-test\nRoot-Is-Purelib: true\nTag: py3-none-any\n')
    wheel.writestr('ringfour_test_dep-1.0.dist-info/RECORD', '')
PY
runner_options=()
if [ "${RINGFOUR_TEST_LOGBOOK:-0}" = 1 ]; then
  python3 - "$test_dir/sdk-source" <<'PY'
import shutil, sys
shutil.copytree('../ringfour-sdk', sys.argv[1], ignore=shutil.ignore_patterns('.git', '.venv', 'build', 'dist', '__pycache__', '*.egg-info'))
PY
  uv build --wheel --out-dir "$test_dir/wheels" "$test_dir/sdk-source" >/dev/null 2>&1
  uv build --wheel --out-dir "$test_dir/wheels" packages/python-runtime >/dev/null 2>&1
  python3 - "$test_dir/wheels/ringfour_sdk-0.1.0.dev0-py3-none-any.whl" <<'PY'
import sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as wheel:
    assert 'ringfour/logbook.py' not in wheel.namelist()
PY
  runner_options=(-e "RUNNER_LOGBOOK_URL=http://$daemon:8081" -v "$test_dir/wheels/ringfour_sdk-0.1.0.dev0-py3-none-any.whl:/opt/ringfour/sdk/ringfour_sdk-0.1.0.dev0-py3-none-any.whl:ro")
fi
org=$(api_curl -H 'content-type: application/json' -d '{"name":"Milestone 07 isolated test"}' "$RINGFOUR_API_URL/v1/organizations" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
robot=$(api_curl -H 'content-type: application/json' -d '{"name":"test-robot","profile_ref":null}' "$RINGFOUR_API_URL/v1/organizations/$org/robots" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
owner=$(api_curl "$RINGFOUR_API_URL/v1/me" | python3 -c 'import json,sys; print(json.load(sys.stdin)["user"]["id"])')
enrollment=$(api_curl -X POST "$RINGFOUR_API_URL/v1/robots/$robot/enrollment-tokens" | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')
docker run -d --name "$runner" --network "$network" -v "$stage:/staged:ro" -v "$runner_data:/data" -v "$test_dir/wheels:/test-wheels:ro" -e RUNNER_AUTH_TOKEN="$secret" "${runner_options[@]}" ringfour-runner:07-test >/dev/null
docker run -d --name "$daemon" --network "$network" -p 127.0.0.1::8081 -p 127.0.0.1::8084 -v "$stage:/staged" -v "$daemon_data:/data" \
  -e "RUNNER_URL=http://$runner:8082" -e RUNNER_AUTH_TOKEN="$secret" -e "PLATFORM_API_URL=http://$api:8080" -e RINGFOUR_LOCAL_DEV_ENABLED=true ringfour-daemon:07-test >/dev/null
control_port=$(docker port "$daemon" 8081/tcp | sed 's/.*://')
local_port=$(docker port "$daemon" 8084/tcp | sed 's/.*://')
for _ in $(seq 1 60); do curl -sf "127.0.0.1:$control_port/health/ready" >/dev/null && break; sleep 1; done
curl -fsS -H 'content-type: application/json' -d "{\"token\":\"$enrollment\"}" "127.0.0.1:$control_port/v1/enroll" >/dev/null
for _ in $(seq 1 60); do
  online=$(api_curl "$RINGFOUR_API_URL/v1/robots/$robot/status" | python3 -c 'import json,sys; print(json.load(sys.stdin)["online"])')
  advertised=$(docker exec "$postgres" psql -At -U ringfour -d ringfour -c "SELECT count(*) FROM dev_local_endpoints WHERE robot_id='$robot'")
  [ "$online" = True ] && [ "$advertised" = 1 ] && break
  sleep 1
done
[ "$online" = True ] && [ "$advertised" = 1 ]
mkdir "$test_dir/mission"
cat >"$test_dir/mission/mission.toml" <<'MANIFEST'
schema_version = 1
name = "cli-dev"
entrypoint = "main.py"
python_requirement = ">=3.13,<3.14"
lock_file = "requirements.lock"
MANIFEST
: >"$test_dir/mission/requirements.lock"
printf 'print("cli source one")\n' >"$test_dir/mission/main.py"
printf 'print("remove me")\n' >"$test_dir/mission/old.py"
ttl=3600
if [ "${RINGFOUR_TEST_OFFLINE_EXPIRY:-0}" = 1 ]; then ttl=300; fi
"${cli[@]}" dev start "$test_dir/mission" --org "$org" --robot "$robot" --ttl "$ttl" --daemon-url "https://127.0.0.1:$local_port" >"$test_dir/start.out"
session=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["dev_sessions"][0]["session_id"])' "$test_dir/mission/ringfour.context.json")
wait_dev_ready() {
  local current=""
  for _ in $(seq 1 60); do
    current=$(api_curl "$RINGFOUR_API_URL/v1/dev-sessions/$session" | python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])')
    [ "$current" = ready ] || [ "$current" = running ] || { sleep 1; continue; }
    return 0
  done
  echo "Development session did not settle (state: $current)" >&2
  return 1
}
(cd "$test_dir/mission" && "${cli[@]}" dev sync --org "$org") >"$test_dir/sync1.out"
grep -q 'generation 1' "$test_dir/sync1.out"
(cd "$test_dir/mission" && "${cli[@]}" dev restart --follow --org "$org") >"$test_dir/run1.out"
grep -q 'cli source one' "$test_dir/run1.out"
wait_dev_ready
markers() {
  docker exec "$runner" /app/.venv/bin/python -c 'import pathlib,sys; p=pathlib.Path("/data/dev-sessions")/sys.argv[1]/"envs"; print("\n".join(sorted(x.name+":"+str(x.stat().st_mtime_ns) for x in p.glob("*.ready"))))' "$session"
}
initial_markers=$(markers)
[ "$(printf '%s\n' "$initial_markers" | wc -l | tr -d ' ')" = 1 ] && [ -n "$initial_markers" ]
(cd "$test_dir/mission" && "${cli[@]}" dev sync --org "$org") >"$test_dir/same.out"
grep -q 'already up to date' "$test_dir/same.out"
rm "$test_dir/mission/old.py"
printf 'print("cli source two")\n' >"$test_dir/mission/main.py"
(cd "$test_dir/mission" && "${cli[@]}" dev sync --org "$org") >"$test_dir/sync2.out"
grep -q 'generation 2' "$test_dir/sync2.out"
for _ in $(seq 1 60); do
  generation=$(api_curl "$RINGFOUR_API_URL/v1/dev-sessions/$session" | python3 -c 'import json,sys; print(json.load(sys.stdin)["generation"])')
  [ "$generation" = 2 ] && break
  sleep 1
done
[ "$generation" = 2 ]
(cd "$test_dir/mission" && "${cli[@]}" dev restart --follow --org "$org") >"$test_dir/run2.out"
grep -q 'cli source two' "$test_dir/run2.out"
wait_dev_ready
[ "$(markers)" = "$initial_markers" ]
(cd "$test_dir/mission" && "${cli[@]}" dev logs --follow --org "$org") >"$test_dir/logs.out"
grep -q 'cli source two' "$test_dir/logs.out"
found=$(docker exec "$postgres" psql -At -U ringfour -d ringfour -c "SELECT count(*) FROM operations WHERE payload::text LIKE '%cli source two%'")
[ "$found" = 0 ]
if [ "${RINGFOUR_TEST_LOGBOOK:-0}" = 1 ]; then
  cp ../missions/diagnostics/requirements.lock "$test_dir/mission/requirements.lock"
  python3 - "$test_dir/mission/requirements.lock" "$test_dir/wheels/ringfour_sdk-0.1.0.dev0-py3-none-any.whl" <<'PY'
import hashlib,pathlib,re,sys
lock=pathlib.Path(sys.argv[1]); data=lock.read_text(); digest=hashlib.sha256(pathlib.Path(sys.argv[2]).read_bytes()).hexdigest()
data,n=re.subn(r'(/opt/ringfour/sdk/ringfour_sdk-0\.1\.0\.dev0-py3-none-any\.whl\s*\\\s*\n\s*--hash=sha256:)[0-9a-f]{64}',r'\g<1>'+digest,data)
assert n == 1
lock.write_text(data)
PY
  runtime_hash=$(python3 -c 'import hashlib,sys; print(hashlib.sha256(open(sys.argv[1],"rb").read()).hexdigest())' "$test_dir/wheels/ringfour_platform_runtime-0.1.0.dev0-py3-none-any.whl")
  printf '/test-wheels/ringfour_platform_runtime-0.1.0.dev0-py3-none-any.whl \\\n    --hash=sha256:%s\n' "$runtime_hash" >>"$test_dir/mission/requirements.lock"
  cat >"$test_dir/mission/main.py" <<'PY'
import os
from pathlib import Path
import time
from ringfour import Robot
from ringfour_runtime import logbook
assert Robot is not None
assert "RUNNER_AUTH_TOKEN" not in os.environ
if Path("/test-wheels/offline-test").exists():
    while not Path("/test-wheels/allow-entry").exists():
        time.sleep(0.1)
entry = logbook.info("Inspection skipped", station="B", reason="Operator reported maintenance")
print("logbook entry " + entry, flush=True)
PY
  (cd "$test_dir/mission" && "${cli[@]}" dev sync --org "$org") >"$test_dir/author-sync.out"
  if [ "${RINGFOUR_TEST_LOGBOOK_OFFLINE:-0}" = 1 ]; then
    : >"$test_dir/wheels/offline-test"
    (cd "$test_dir/mission" && "${cli[@]}" dev restart --org "$org") >"$test_dir/author-run.out"
    for _ in $(seq 1 60); do
      running=$(docker exec "$postgres" psql -At -U ringfour -d ringfour -c "SELECT count(*) FROM dev_session_runs WHERE session_id='$session' AND state='running'")
      [ "$running" = 1 ] && break
      sleep 1
    done
    [ "$running" = 1 ]
    docker stop "$api" >/dev/null
    : >"$test_dir/wheels/allow-entry"
    for _ in $(seq 1 60); do
      queued=$(docker exec "$daemon" /app/.venv/bin/python -c 'import sqlite3; c=sqlite3.connect("/data/ringfour.db"); print(c.execute("select count(*) from logbook_outbox where json_extract(event_json,\047$.type\047)=\047mission.logbook.entry\047").fetchone()[0])')
      [ "$queued" = 1 ] && break
      sleep 1
    done
    [ "$queued" = 1 ]
    for _ in $(seq 1 60); do
      local_result=$(docker exec "$runner" /app/.venv/bin/python -c 'import sqlite3,sys; c=sqlite3.connect("/data/executions.db"); r=c.execute("select e.state from executions e join dev_execution_sessions d on d.execution_id=e.execution_id where d.session_id=? order by e.created_at desc limit 1",(sys.argv[1],)).fetchone(); print(r[0] if r else "missing")' "$session")
      [ "$local_result" = succeeded ] && break
      sleep 1
    done
    [ "$local_result" = succeeded ]
    docker start "$api" >/dev/null
    for _ in $(seq 1 60); do curl -sf "$RINGFOUR_API_URL/health/ready" >/dev/null && break; sleep 1; done
    curl -fsS "$RINGFOUR_API_URL/health/ready" >/dev/null
  else
    (cd "$test_dir/mission" && "${cli[@]}" dev restart --follow --org "$org") >"$test_dir/author-run.out"
    grep -q 'logbook entry ' "$test_dir/author-run.out"
  fi
  for _ in $(seq 1 60); do
    entries=$(docker exec "$postgres" psql -At -U ringfour -d ringfour -c "SELECT count(*) FROM logbook_events e JOIN dev_session_runs r ON r.id=e.execution_id AND r.operation_id=e.operation_id WHERE e.robot_id='$robot' AND r.actor_user_id='$owner' AND e.type='mission.logbook.entry' AND e.payload->>'message'='Inspection skipped' AND e.payload->'fields'->>'station'='B'")
    [ "$entries" = 1 ] && break
    sleep 1
  done
  [ "$entries" = 1 ]
  (cd "$test_dir/mission" && "${cli[@]}" dev stop --org "$org") >"$test_dir/author-stop.out"
  echo 'PASS: actual SDK mission wrote an execution-attributed entry through Runner, daemon and Platform'
  exit 0
fi

# Real stale upload via the same pinned CLI transport and an authorized grant.
RF_TEST_PROJECT="$test_dir/mission" RF_TEST_ORG="$org" uv run --project apps/cli python - <<'PY'
import os, uuid
from pathlib import Path
from ringfour_cli.auth import TokenStore
from ringfour_cli.config import load_settings
from ringfour_cli.dev_sync import build_snapshot, local_request
from ringfour_cli.main import _dev_target, _local_grant
settings = load_settings(); project = Path(os.environ['RF_TEST_PROJECT'])
target = _dev_target(settings, os.environ['RF_TEST_ORG'], None, None, project)
connection = _local_grant(settings, TokenStore(), target)
body = build_snapshot(project, [], 1)  # generation 2 is already committed
status, result = local_request(*connection, 'PUT', f"/v1/local/dev-sessions/{target['session_id']}/source", body=body, key=str(uuid.uuid4()))
assert status == 409 and result['error']['code'] == 'stale_generation', (status, result)
PY

# Restart both services with persistent volumes and run the same generation again.
old_device_session=$(docker exec "$postgres" psql -At -U ringfour -d ringfour -c "SELECT session_id FROM robot_presence WHERE robot_id='$robot'")
docker restart "$runner" >/dev/null
for _ in $(seq 1 60); do
  docker exec "$runner" /app/.venv/bin/python -c 'import urllib.request; urllib.request.urlopen("http://127.0.0.1:8082/health/ready", timeout=1)' >/dev/null 2>&1 && break
  sleep 1
done
docker restart "$daemon" >/dev/null
local_port=$(docker port "$daemon" 8084/tcp | sed 's/.*://')
python3 - "$test_dir/mission/ringfour.context.json" "$local_port" <<'PY'
import json, pathlib, sys
path = pathlib.Path(sys.argv[1]); data = json.loads(path.read_text())
data['dev_sessions'][0]['daemon_url'] = f'https://127.0.0.1:{sys.argv[2]}'
path.write_text(json.dumps(data))
PY
for _ in $(seq 1 90); do
  device_session=$(docker exec "$postgres" psql -At -U ringfour -d ringfour -c "SELECT session_id FROM robot_presence WHERE robot_id='$robot'")
  [ -n "$device_session" ] && [ "$device_session" != "$old_device_session" ] && break
  sleep 1
done
[ -n "$device_session" ] && [ "$device_session" != "$old_device_session" ]
(cd "$test_dir/mission" && "${cli[@]}" dev restart --follow --org "$org") >"$test_dir/recovered.out"
grep -q 'cli source two' "$test_dir/recovered.out"
[ "$(markers)" = "$initial_markers" ]

# Replace the empty lock with a hash-pinned local wheel and prove it was installed.
wheel_hash=$(python3 -c 'import hashlib,sys; print(hashlib.sha256(open(sys.argv[1],"rb").read()).hexdigest())' "$test_dir/wheels/ringfour_test_dep-1.0-py3-none-any.whl")
printf 'ringfour-test-dep @ file:///test-wheels/ringfour_test_dep-1.0-py3-none-any.whl --hash=sha256:%s\n' "$wheel_hash" >"$test_dir/mission/requirements.lock"
printf 'import ringfour_test_dep\nprint(ringfour_test_dep.VALUE, flush=True)\n' >"$test_dir/mission/main.py"
(cd "$test_dir/mission" && "${cli[@]}" dev sync --org "$org") >"$test_dir/dependency-sync.out"
(cd "$test_dir/mission" && "${cli[@]}" dev restart --follow --org "$org") >"$test_dir/dependency-run.out"
grep -q 'dependency installed' "$test_dir/dependency-run.out"
[ "$(markers | wc -l | tr -d ' ')" = 2 ]
printf '%s\n' "$(markers)" | grep -Fxq "$initial_markers"
printf 'import time\nprint("long running", flush=True)\ntime.sleep(600)\n' >"$test_dir/mission/main.py"
(cd "$test_dir/mission" && "${cli[@]}" dev sync --org "$org") >"$test_dir/sync3.out"
(cd "$test_dir/mission" && "${cli[@]}" dev restart --org "$org") >"$test_dir/run3.out"
for _ in $(seq 1 60); do
  running=$(docker exec "$postgres" psql -At -U ringfour -d ringfour -c "SELECT count(*) FROM dev_session_runs WHERE session_id='$session' AND state='running'")
  [ "$running" = 1 ] && break
  sleep 1
done
[ "$running" = 1 ]
if "${cli[@]}" run "$test_dir/mission/main.py" --robot "$robot" --org "$org" --detach >"$test_dir/conflict.out" 2>&1; then
  echo 'Expected production run to be rejected while development code is running' >&2
  exit 1
fi
grep -qi 'execution_conflict' "$test_dir/conflict.out"
if [ "${RINGFOUR_TEST_OFFLINE_EXPIRY:-0}" = 1 ]; then
  docker stop "$api" >/dev/null
  # Inspect the real Runner's durable state and workspace while Platform is down.
  for _ in $(seq 1 360); do
    local_state=$(docker exec "$runner" /app/.venv/bin/python -c 'import sqlite3,sys; c=sqlite3.connect("/data/executions.db"); print(c.execute("select state from dev_sessions where session_id=?", (sys.argv[1],)).fetchone()[0])' "$session")
    [ "$local_state" = closed ] && break
    sleep 1
  done
  [ "$local_state" = closed ]
  docker exec "$runner" /app/.venv/bin/python -c 'import pathlib,sqlite3,sys; s=sys.argv[1]; c=sqlite3.connect("/data/executions.db"); assert not (pathlib.Path("/data/dev-sessions")/s).exists(); assert not c.execute("select 1 from executions where state in (\047queued\047,\047preparing\047,\047running\047)").fetchone()' "$session"
  docker start "$api" >/dev/null
  for _ in $(seq 1 120); do
    state=$(api_curl "$RINGFOUR_API_URL/v1/dev-sessions/$session" 2>/dev/null | python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])' 2>/dev/null || true)
    [ "$state" = expired ] && break
    sleep 1
  done
  [ "$state" = expired ]
  echo 'PASS: active development execution expired offline, workspace was removed, and Platform reconciled'
  exit 0
fi
(cd "$test_dir/mission" && "${cli[@]}" dev stop --org "$org") >"$test_dir/stop.out"
for _ in $(seq 1 60); do
  active=$(docker exec "$postgres" psql -At -U ringfour -d ringfour -c "SELECT count(*) FROM dev_session_runs WHERE session_id='$session' AND state IN ('queued','preparing','running')")
  [ "$active" = 0 ] && break
  sleep 1
done
[ "$active" = 0 ]
for _ in $(seq 1 60); do
  state=$(api_curl "$RINGFOUR_API_URL/v1/dev-sessions/$session" | python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])')
  [ "$state" = closed ] && break
  sleep 1
done
[ "$state" = closed ]
echo 'PASS: full-stack sync, stale rejection, environment reuse, service recovery, dependency reprovision, execution and closure'
