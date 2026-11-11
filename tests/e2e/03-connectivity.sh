#!/usr/bin/env bash
# Milestone 03 integrated acceptance: durable connectivity over the real stack.
# Prerequisites: `ringfour-cli login` approved for the owner account (account 1)
# and the compose stack running. Everything below exercises real processes —
# daemon, API, PostgreSQL, SQLite and the WebSocket protocol — no mocks.
set -euo pipefail
cd "$(dirname "$0")/../.."

COMPOSE="docker compose -f $PWD/infra/compose/docker-compose.yml"
fail() { echo "FAIL: $1" >&2; exit 1; }

echo "==> stack readiness"
curl -sf localhost:8080/health/ready | grep -q '"status":"ok"' || fail "api not ready"
curl -sf localhost:8081/health/ready | grep -q '"status":"ok"' || fail "daemon not ready"

echo "==> reset daemon state so the script is repeatable"
$COMPOSE rm -sf daemon >/dev/null
docker volume rm ringfour_daemon-data >/dev/null 2>&1 || true
$COMPOSE up -d daemon >/dev/null
for _ in $(seq 1 30); do
  sleep 1
  curl -sf localhost:8081/health/ready >/dev/null 2>&1 && break
done
curl -sf localhost:8081/health/ready | grep -q '"status":"ok"' || fail "daemon not ready after reset"

cd apps/cli
set -a
# shellcheck disable=SC1091
. ../../.auth.env
set +a
export RINGFOUR_AUTH0_DOMAIN="$AUTH0_DOMAIN"
export RINGFOUR_AUTH0_CLIENT_ID="$AUTH0_CLI_CLIENT_ID"
export RINGFOUR_AUTH0_AUDIENCE="$AUTH0_AUDIENCE"

echo "==> enroll a fresh robot"
org_name="e2e-03-$(date +%s)"
uv run ringfour-cli org create "$org_name" >/dev/null || fail "org create failed"
org_id=$(uv run ringfour-cli org list | grep "$org_name" | grep -oE '[0-9a-f-]{36}' | head -1)
[ -n "$org_id" ] || fail "could not parse org id"
uv run ringfour-cli org select "$org_id" >/dev/null || fail "org select failed"

create_out=$(uv run ringfour-cli robot create "e2e-bot-$(date +%s)")
robot_id=$(echo "$create_out" | grep -oE '[0-9a-f-]{36}' | head -1)
[ -n "$robot_id" ] || fail "could not parse robot id"
token_out=$(uv run ringfour-cli robot enrollment-token "$robot_id")
token=$(echo "$token_out" | grep -oE 'rft_[A-Za-z0-9_-]{43}')
[ -n "$token" ] || fail "could not parse enrollment token"
uv run ringfour-cli daemon enroll "$token" >/dev/null || fail "daemon enroll failed"
echo "robot: $robot_id"

# wait_status <robot_id> <online|offline> <max_seconds>
wait_status() {
  local deadline=$((SECONDS + $3))
  while (( SECONDS < deadline )); do
    out=$(uv run ringfour-cli robot status "$1" 2>/dev/null) || true
    if grep -q "Status:.*$2" <<<"$out"; then return 0; fi
    sleep 2
  done
  return 1
}

echo "==> daemon connects by itself after enrollment and reads online"
wait_status "$robot_id" online 60 || fail "robot did not come online after enroll"
status_out=$(uv run ringfour-cli robot status "$robot_id")
grep -q "0.1.0" <<<"$status_out" || fail "observed daemon version missing"
echo "$status_out"

echo "==> owner access token for direct API calls (from the CLI credential store)"
access_token=$(uv run python - <<'PY'
from ringfour_cli.auth import TokenStore
from ringfour_cli.config import load_settings

tokens = TokenStore().load(load_settings())
if tokens is None:
    raise SystemExit("not logged in; run `ringfour-cli login` first")
print(tokens["access_token"])
PY
)
[ -n "$access_token" ] || fail "no stored CLI token"

# create_op <robot_id> [idempotency-key]
create_op() {
  local args=(-s -o /tmp/rf-03-op.json -w '%{http_code}' -X POST \
    "localhost:8080/v1/robots/$1/operations" \
    -H "authorization: Bearer $access_token" \
    -H 'content-type: application/json' \
    -d '{"kind":"diagnostic.noop","payload_version":1,"payload":{}}')
  if [ $# -ge 2 ]; then
    args+=(-H "idempotency-key: $2")
  fi
  curl "${args[@]}"
}

# wait_op <robot_id> <op_id> <max_seconds> -> writes state/result lines
wait_op() {
  local deadline=$((SECONDS + $3)) body
  while (( SECONDS < deadline )); do
    body=$(curl -s "localhost:8080/v1/robots/$1/operations/$2" \
      -H "authorization: Bearer $access_token")
    state=$(python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])' <<<"$body")
    case "$state" in
      succeeded|failed|cancelled) echo "$body"; return 0 ;;
    esac
    sleep 1
  done
  return 1
}

op_field() { python3 -c "import json,sys; print(json.load(sys.stdin)['$1'])"; }

echo "==> diagnostic operation runs end to end over the live WebSocket"
code=$(create_op "$robot_id")
[ "$code" = "201" ] || fail "expected 201 creating operation, got $code"
op_id=$(python3 -c 'import json; print(json.load(open("/tmp/rf-03-op.json"))["id"])')
result=$(wait_op "$robot_id" "$op_id" 30) || fail "operation did not reach a terminal state"
[ "$(op_field state <<<"$result")" = "succeeded" ] || fail "operation did not succeed: $result"
[ "$(op_field result <<<"$result")" = "{'noop': True}" ] || fail "unexpected result: $result"
echo "operation $op_id succeeded with $(op_field result <<<"$result")"

echo "==> Idempotency-Key replays the original operation"
code=$(create_op "$robot_id" "e2e-03-key")
[ "$code" = "201" ] || fail "expected 201 on first keyed request, got $code"
first_id=$(op_field id < /tmp/rf-03-op.json)
code=$(create_op "$robot_id" "e2e-03-key")
[ "$code" = "200" ] || fail "expected 200 replaying the same key, got $code"
[ "$(op_field id < /tmp/rf-03-op.json)" = "$first_id" ] || fail "idempotency replay returned a different operation"

echo "==> network interruption: offline after the freshness threshold"
daemon_container=$($COMPOSE ps -q daemon)
network=$(docker inspect -f \
  '{{range $name, $net := .NetworkSettings.Networks}}{{$name}}{{end}}' "$daemon_container")
[ -n "$network" ] || fail "could not resolve the compose network"
docker network disconnect "$network" "$daemon_container"
wait_status "$robot_id" offline 90 || fail "robot did not go offline while partitioned"
echo "offline after threshold, as observed by the API"

echo "==> operations created while offline stay queued and run on reconnect"
code=$(create_op "$robot_id")
[ "$code" = "201" ] || fail "expected 201 creating offline operation, got $code"
offline_op=$(op_field id < /tmp/rf-03-op.json)
queued=$(curl -s "localhost:8080/v1/robots/$robot_id/operations/$offline_op" \
  -H "authorization: Bearer $access_token")
[ "$(op_field state <<<"$queued")" = "queued" ] || fail "offline operation should be queued: $queued"
docker network connect "$network" "$daemon_container"
wait_status "$robot_id" online 90 || fail "robot did not come back online after reconnect"
result=$(wait_op "$robot_id" "$offline_op" 30) || fail "offline operation never ran"
[ "$(op_field state <<<"$result")" = "succeeded" ] || fail "offline operation did not succeed"

echo "==> daemon restart: reconnect replays, no duplicate effects"
$COMPOSE restart daemon >/dev/null
for _ in $(seq 1 30); do
  sleep 1
  curl -sf localhost:8081/health/ready >/dev/null 2>&1 && break
done
curl -sf localhost:8081/health/ready | grep -q '"status":"ok"' || fail "daemon not ready after restart"
wait_status "$robot_id" online 90 || fail "robot not online after daemon restart"
code=$(create_op "$robot_id")
[ "$code" = "201" ] || fail "expected 201 after restart, got $code"
restart_op=$(op_field id < /tmp/rf-03-op.json)
result=$(wait_op "$robot_id" "$restart_op" 30) || fail "post-restart operation never ran"
[ "$(op_field state <<<"$result")" = "succeeded" ] || fail "post-restart operation did not succeed"
row_count=$($COMPOSE exec -T daemon python -c \
  "import sqlite3; print(sqlite3.connect('/data/ringfour.db').execute('SELECT count(*) FROM device_operations WHERE operation_id = \'$offline_op\'').fetchone()[0])")
[ "$row_count" = "1" ] || fail "expected exactly one local dedup row, got $row_count"

echo "==> revocation closes the session and denies reconnect"
identity_out=$(uv run ringfour-cli daemon identity)
credential_id=$(grep "Credential:" <<<"$identity_out" | grep -oE '[0-9a-f-]{36}')
[ -n "$credential_id" ] || fail "could not parse credential id"
uv run ringfour-cli robot revoke "$robot_id" "$credential_id" || fail "revoke failed"
wait_status "$robot_id" offline 90 || fail "robot did not go offline after revocation"
# The daemon's first post-revocation retry (401) lands within its backoff
# window; poll the container log instead of a single-shot grep.
revoke_failures=""
for _ in $(seq 1 20); do
  revoke_failures=$($COMPOSE logs daemon --since 2m 2>&1 | grep -c "device session failed" || true)
  [ "$revoke_failures" -ge 1 ] && break
  sleep 3
done
[ "$revoke_failures" -ge 1 ] \
  || fail "daemon should report failed sessions after revocation"
identity_out=$(uv run ringfour-cli daemon identity)
grep -q "$robot_id" <<<"$identity_out" || fail "daemon lost its enrolled identity"

echo "PASS: milestone 03 automatable acceptance checks"
