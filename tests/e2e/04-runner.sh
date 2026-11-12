#!/usr/bin/env bash
# Local-only real API -> isolated daemon -> isolated Runner acceptance.
# Requires running API/DB, .auth.env and a CLI owner with an organization selected.
set -euo pipefail
cd "$(dirname "$0")/../.."
name="rf04-$$"
runner="$name-runner"
daemon="$name-daemon"
stage="$name-stage"
rdata="$name-runner-data"
ddata="$name-daemon-data"
port=18084
network=ringfour_default
cleanup() {
  docker rm -f "$daemon" "$runner" >/dev/null 2>&1 || true
  docker volume rm "$stage" "$rdata" "$ddata" >/dev/null 2>&1 || true
}
trap cleanup EXIT
set -a
. ./.auth.env
set +a
export RINGFOUR_AUTH0_DOMAIN="$AUTH0_DOMAIN" RINGFOUR_AUTH0_CLIENT_ID="$AUTH0_CLI_CLIENT_ID" RINGFOUR_AUTH0_AUDIENCE="$AUTH0_AUDIENCE"
export RINGFOUR_DAEMON_URL="http://localhost:$port"
secret=$(openssl rand -hex 32)
cli=(uv run --project apps/cli ringfour-cli)
"${cli[@]}" whoami >/dev/null
robot=$("${cli[@]}" robot create "$name" | grep -oE '[0-9a-f-]{36}' | head -1)
enrollment=$("${cli[@]}" robot enrollment-token "$robot" | grep -oE 'rft_[A-Za-z0-9_-]{43}')
access_token=$(uv run --project apps/cli python -c 'from ringfour_cli.auth import TokenStore; from ringfour_cli.config import load_settings; print(TokenStore().load(load_settings())["access_token"])')
docker volume create "$stage" >/dev/null
docker volume create "$rdata" >/dev/null
docker volume create "$ddata" >/dev/null
docker run -d --name "$runner" --network "$network" -v "$stage:/staged:ro" -v "$rdata:/data" \
  -e RUNNER_AUTH_TOKEN="$secret" ringfour-runner >/dev/null
[ -z "$(docker port "$runner")" ]
[ "$(docker exec "$runner" id -u)" != 0 ]
docker inspect "$runner" --format '{{range .Mounts}}{{if eq .Destination "/staged"}}{{.RW}}{{end}}{{end}}' | grep -qx false
if docker inspect "$runner" --format '{{range .Mounts}}{{println .Destination}}{{end}}' | grep -q docker.sock; then
  echo 'Runner must not mount a Docker socket' >&2; exit 1
fi
docker run -d --name "$daemon" --network "$network" -p "127.0.0.1:$port:8081" \
  -v "$stage:/staged" -v "$ddata:/data" -e RUNNER_URL="http://$runner:8082" \
  -e RUNNER_AUTH_TOKEN="$secret" -e PLATFORM_API_URL=http://api:8080 ringfour-daemon >/dev/null
for _ in $(seq 1 40); do curl -sf "localhost:$port/health/ready" >/dev/null && break; sleep 1; done
curl -fsS -H 'content-type: application/json' -d "{\"token\":\"$enrollment\"}" "localhost:$port/v1/enroll" >/dev/null
for _ in $(seq 1 40); do
  status=$(curl -fsS -H "authorization: Bearer $access_token" "localhost:8080/v1/robots/$robot/status")
  [ "$(python3 -c 'import json,sys; print(json.load(sys.stdin)["online"])' <<<"$status")" = True ] && break
  sleep 1
done
[ "$(python3 -c 'import json,sys; print(json.load(sys.stdin)["online"])' <<<"$status")" = True ]
# An unauthenticated request must fail even from the private network.
docker exec "$daemon" /app/.venv/bin/python -c "import httpx; assert httpx.get('http://$runner:8082/v1/executions/00000000-0000-0000-0000-000000000000').status_code == 401"
wait_op() {
  local op=$1 value state
  for _ in $(seq 1 40); do
    value=$(curl -fsS -H "authorization: Bearer $access_token" "localhost:8080/v1/robots/$robot/operations/$op")
    state=$(python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])' <<<"$value")
    if [ "$state" = succeeded ]; then printf '%s\n' "$value"; return 0; fi
    if [ "$state" = failed ]; then printf '%s\n' "$value" >&2; return 1; fi
    sleep 1
  done
  echo "operation $op timed out" >&2; return 1
}
create_inspect() {
  curl -fsS -H "authorization: Bearer $access_token" -H 'content-type: application/json' \
    -d "{\"kind\":\"diagnostic.execution.inspect\",\"payload_version\":1,\"payload\":{\"execution_id\":\"$1\",\"stream\":\"stdout\",\"offset\":0,\"limit\":4096}}" \
    "localhost:8080/v1/robots/$robot/operations" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])'
}
wait_execution() {
  local execution=$1 expected=$2 result state
  for _ in $(seq 1 40); do
    result=$(wait_op "$(create_inspect "$execution")")
    state=$(python3 -c 'import json,sys; print(json.load(sys.stdin)["result"]["execution"]["state"])' <<<"$result")
    if [ "$state" = "$expected" ]; then printf '%s\n' "$result"; return 0; fi
    if [ "$state" = failed ] && [ "$expected" != failed ]; then echo "$result" >&2; return 1; fi
    sleep 1
  done
  echo "execution $execution did not reach $expected" >&2; return 1
}
hello=$("${cli[@]}" robot fixture-start "$robot" hello | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
wait_op "$hello" >/dev/null
result=$(wait_execution "$hello" succeeded)
python3 -c 'import base64,json,sys; r=json.load(sys.stdin)["result"]; assert r["execution"]["exit_code"] == 0; assert b"Hello from Ringfour Runner" in base64.b64decode(r["output"]["data_base64"])' <<<"$result"
echo "PASS: hosted hello fixture produced output and exit 0"
long=$("${cli[@]}" robot fixture-start "$robot" long | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
wait_op "$long" >/dev/null
wait_execution "$long" running >/dev/null
overlap=$("${cli[@]}" robot fixture-start "$robot" hello | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
for _ in $(seq 1 30); do
  overlap_result=$(curl -fsS -H "authorization: Bearer $access_token" "localhost:8080/v1/robots/$robot/operations/$overlap")
  [ "$(python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])' <<<"$overlap_result")" = failed ] && break
  sleep 1
done
python3 -c 'import json,sys; x=json.load(sys.stdin); assert x["state"] == "failed" and x["result"]["error"]["code"] == "execution_conflict"' <<<"$overlap_result"
docker restart "$daemon" >/dev/null
wait_execution "$long" running >/dev/null
stop=$("${cli[@]}" robot fixture-stop "$robot" "$long" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
wait_op "$stop" >/dev/null
wait_execution "$long" cancelled >/dev/null
echo "PASS: overlap rejected, daemon restart preserved running fixture, and hosted stop cancelled it"
long2=$("${cli[@]}" robot fixture-start "$robot" long | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
wait_op "$long2" >/dev/null
wait_execution "$long2" running >/dev/null
docker restart "$runner" >/dev/null
result=$(wait_execution "$long2" failed)
python3 -c 'import json,sys; assert json.load(sys.stdin)["result"]["execution"]["error"]["code"] == "interrupted_unknown_outcome"' <<<"$result"
echo "PASS: Runner restart reconciled interrupted work without replay"
echo "PASS: local Runner acceptance; test robot $robot remains in the selected organization"
