#!/usr/bin/env bash
# Real API and daemon, with an isolated daemon volume and a test-only ack barrier.
# Requires running Compose, .auth.env and an authenticated CLI owner with an org selected.
set -euo pipefail
cd "$(dirname "$0")/../.."
name="rf-preack-$$"
volume="$name-data"
network=ringfour_default
port=18081
cleanup() {
  docker rm -f "$name" >/dev/null 2>&1 || true
  docker volume rm "$volume" >/dev/null 2>&1 || true
}
trap cleanup EXIT
set -a
. ./.auth.env
set +a
export RINGFOUR_AUTH0_DOMAIN="$AUTH0_DOMAIN" RINGFOUR_AUTH0_CLIENT_ID="$AUTH0_CLI_CLIENT_ID" RINGFOUR_AUTH0_AUDIENCE="$AUTH0_AUDIENCE"
cli=(uv run --project apps/cli ringfour-cli)
"${cli[@]}" whoami >/dev/null
robot=$("${cli[@]}" robot create "pre-ack-$name" | grep -oE '[0-9a-f-]{36}' | head -1)
enrollment=$("${cli[@]}" robot enrollment-token "$robot" | grep -oE 'rft_[A-Za-z0-9_-]{43}')
access_token=$(uv run --project apps/cli python -c 'from ringfour_cli.auth import TokenStore; from ringfour_cli.config import load_settings; print(TokenStore().load(load_settings())["access_token"])')
docker volume create "$volume" >/dev/null
# Same production image and code; only the entrypoint inserts a test barrier.
docker run -d --name "$name" --network "$network" -p "127.0.0.1:$port:8081" \
  -v "$volume:/data" -v "$PWD/tests/e2e/fixtures/daemon_pre_ack.py:/tmp/pre_ack.py:ro" \
  -e PLATFORM_API_URL=http://api:8080 -e RUNNER_URL=http://runner:8082 \
  ringfour-daemon /app/.venv/bin/python /tmp/pre_ack.py >/dev/null
for _ in $(seq 1 30); do curl -sf "localhost:$port/health/ready" >/dev/null && break; sleep 1; done
curl -fsS -H 'content-type: application/json' -d "{\"token\":\"$enrollment\"}" "localhost:$port/v1/enroll" >/dev/null
for _ in $(seq 1 30); do
  status=$(curl -fsS -H "authorization: Bearer $access_token" "localhost:8080/v1/robots/$robot/status")
  [ "$(python3 -c 'import json,sys; print(json.load(sys.stdin)["online"])' <<<"$status")" = True ] && break
  sleep 1
done
op=$(curl -fsS -H "authorization: Bearer $access_token" -H 'content-type: application/json' \
  -d '{"kind":"diagnostic.noop","payload_version":1,"payload":{}}' "localhost:8080/v1/robots/$robot/operations" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
for _ in $(seq 1 20); do
  marker=$(docker exec "$name" cat /data/pre-ack-operation 2>/dev/null || true)
  [ "$marker" = "$op" ] && break
  sleep 1
done
[ "${marker:-}" = "$op" ] || { echo 'pre-ack barrier not reached' >&2; exit 1; }
# Prove the local durable state exists and the server has not received ack.
local_state=$(docker exec "$name" python -c "import sqlite3; print(sqlite3.connect('/data/ringfour.db').execute('select state from device_operations where operation_id=?', ('$op',)).fetchone()[0])")
[ "$local_state" = accepted ]
server=$(curl -fsS -H "authorization: Bearer $access_token" "localhost:8080/v1/robots/$robot/operations/$op")
python3 -c 'import json,sys; x=json.load(sys.stdin); print("Before interruption:", x["state"], "acknowledged:", x["acknowledged_at"] is not None); assert x["state"] == "queued" and x["acknowledged_at"] is None' <<<"$server"
docker network disconnect "$network" "$name"
docker rm -f "$name" >/dev/null
# Restart the normal daemon against the same durable volume and real platform.
docker run -d --name "$name" --network "$network" -v "$volume:/data" \
  -e PLATFORM_API_URL=http://api:8080 -e RUNNER_URL=http://runner:8082 ringfour-daemon >/dev/null
for _ in $(seq 1 40); do
  server=$(curl -fsS -H "authorization: Bearer $access_token" "localhost:8080/v1/robots/$robot/operations/$op")
  state=$(python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])' <<<"$server")
  [ "$state" = succeeded ] && break
  [ "$state" = failed ] && { echo 'operation failed on replay' >&2; exit 1; }
  sleep 1
done
python3 -c 'import json,sys; x=json.load(sys.stdin); print("After restart:", x["state"], x["result"]); assert x["state"] == "succeeded" and x["result"] == {"noop": True} and x["acknowledged_at"]' <<<"$server"
docker exec "$name" python -c "import sqlite3; c=sqlite3.connect('/data/ringfour.db'); r=c.execute('select state,count(*) from device_operations where operation_id=?', ('$op',)).fetchone(); assert r == ('succeeded', 1), r"
echo "PASS: robot $robot operation $op persisted before ack and succeeded after partition and restart"

# Independently exercise freshness and offline queuing on the final services.
docker network disconnect "$network" "$name"
for _ in $(seq 1 70); do
  status=$(curl -fsS -H "authorization: Bearer $access_token" "localhost:8080/v1/robots/$robot/status")
  [ "$(python3 -c 'import json,sys; print(json.load(sys.stdin)["online"])' <<<"$status")" = False ] && break
  sleep 1
done
[ "$(python3 -c 'import json,sys; print(json.load(sys.stdin)["online"])' <<<"$status")" = False ]
offline_op=$(curl -fsS -H "authorization: Bearer $access_token" -H 'content-type: application/json' -d '{"kind":"diagnostic.noop","payload_version":1,"payload":{}}' "localhost:8080/v1/robots/$robot/operations" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
offline_state=$(curl -fsS -H "authorization: Bearer $access_token" "localhost:8080/v1/robots/$robot/operations/$offline_op")
python3 -c 'import json,sys; assert json.load(sys.stdin)["state"] == "queued"' <<<"$offline_state"
docker network connect "$network" "$name"
for _ in $(seq 1 40); do
  offline_state=$(curl -fsS -H "authorization: Bearer $access_token" "localhost:8080/v1/robots/$robot/operations/$offline_op")
  [ "$(python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])' <<<"$offline_state")" = succeeded ] && break
  sleep 1
done
python3 -c 'import json,sys; x=json.load(sys.stdin); assert x["state"] == "succeeded" and x["result"] == {"noop": True}' <<<"$offline_state"
echo "PASS: offline freshness and queued operation recovered after reconnect"

# Exercise the documented local recovery procedure after revocation. This test
# volume is dedicated to this run; real operators must assess local data loss.
old_credential=$(docker exec "$name" python -c "import sqlite3; print(sqlite3.connect('/data/ringfour.db').execute('select credential_id from device_identity').fetchone()[0])")
"${cli[@]}" robot revoke "$robot" "$old_credential" >/dev/null
# Exercise the revoked credential without printing or passing its secret as an argument.
docker exec "$name" /app/.venv/bin/python -c 'import json, pathlib, sqlite3, httpx; db=sqlite3.connect("/data/ringfour.db"); credential=db.execute("select credential_id from device_identity").fetchone()[0]; secret=pathlib.Path("/data/robot-credential").read_text().strip(); response=httpx.post("http://api:8080/v1/device/authenticate", json={"credential_id": credential, "secret": secret}); assert response.status_code == 401'
docker rm -f "$name" >/dev/null
docker volume rm "$volume" >/dev/null
docker volume create "$volume" >/dev/null
enrollment=$("${cli[@]}" robot enrollment-token "$robot" | grep -oE 'rft_[A-Za-z0-9_-]{43}')
docker run -d --name "$name" --network "$network" -p "127.0.0.1:$port:8081" -v "$volume:/data" \
  -e PLATFORM_API_URL=http://api:8080 -e RUNNER_URL=http://runner:8082 ringfour-daemon >/dev/null
for _ in $(seq 1 30); do curl -sf "localhost:$port/health/ready" >/dev/null && break; sleep 1; done
new_identity=$(curl -fsS -H 'content-type: application/json' -d "{\"token\":\"$enrollment\"}" "localhost:$port/v1/enroll")
new_credential=$(python3 -c 'import json,sys; print(json.load(sys.stdin)["credential_id"])' <<<"$new_identity")
[ "$new_credential" != "$old_credential" ]
docker restart "$name" >/dev/null
for _ in $(seq 1 30); do identity=$(curl -sf "localhost:$port/v1/identity" || true); [ -n "$identity" ] && break; sleep 1; done
python3 -c 'import json,sys; x=json.load(sys.stdin); assert x["enrolled"] and x["robot_id"]==sys.argv[1] and x["credential_id"]==sys.argv[2] and x["error"] is None' "$robot" "$new_credential" <<<"$identity"
for _ in $(seq 1 30); do
  state=$(curl -fsS -H "authorization: Bearer $access_token" "localhost:8080/v1/robots/$robot/status")
  [ "$(python3 -c 'import json,sys; print(json.load(sys.stdin)["online"])' <<<"$state")" = True ] && break
  sleep 1
done
[ "$(python3 -c 'import json,sys; print(json.load(sys.stdin)["online"])' <<<"$state")" = True ]
recovery_op=$(curl -fsS -H "authorization: Bearer $access_token" -H 'content-type: application/json' -d '{"kind":"diagnostic.noop","payload_version":1,"payload":{}}' "localhost:8080/v1/robots/$robot/operations" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
for _ in $(seq 1 30); do
  recovery=$(curl -fsS -H "authorization: Bearer $access_token" "localhost:8080/v1/robots/$robot/operations/$recovery_op")
  [ "$(python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])' <<<"$recovery")" = succeeded ] && break
  sleep 1
done
python3 -c 'import json,sys; assert json.load(sys.stdin)["state"]=="succeeded"' <<<"$recovery"
echo "PASS: revoked identity replaced through fresh local enrollment, survived restart, and executed a new operation"
