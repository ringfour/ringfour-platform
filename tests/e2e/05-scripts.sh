#!/usr/bin/env bash
# Isolated actual API, PostgreSQL, daemon, Runner and CLI. Uses live Auth0 login.
set -euo pipefail
cd "$(dirname "$0")/../.."
name="rf05-$$"
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
test_dir=$(mktemp -d "${TMPDIR:-/tmp}/ringfour-05.XXXXXXXX")
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
export RINGFOUR_API_URL="http://127.0.0.1:18085"
cli=(uv run --project apps/cli ringfour-cli)
secret=$(openssl rand -hex 32)
docker build -q -t ringfour-api:05-test apps/api >/dev/null
docker build -q -t ringfour-daemon:05-test apps/daemon >/dev/null
docker build -q -t ringfour-runner:05-test apps/runner >/dev/null
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
docker run -d --name "$api" --network "$network" -p 127.0.0.1:18085:8080 -v "$api_data:/storage" \
  -e "ConnectionStrings__Postgres=Host=$postgres;Port=5432;Database=ringfour;Username=ringfour;Password=temporary-test-password" \
  -e "Auth0__Domain=$AUTH0_DOMAIN" -e "Auth0__Audience=$AUTH0_AUDIENCE" \
  "${storage_args[@]}" ringfour-api:05-test >/dev/null
for _ in $(seq 1 60); do curl -sf "$RINGFOUR_API_URL/health/ready" >/dev/null && break; sleep 1; done
curl -fsS "$RINGFOUR_API_URL/health/ready" >/dev/null
"${cli[@]}" whoami >/dev/null
access_token=$(uv run --project apps/cli python -c 'from ringfour_cli.auth import TokenStore; from ringfour_cli.config import load_settings; print(TokenStore().load(load_settings())["access_token"])')
api_curl() { curl -fsS -H "authorization: Bearer $access_token" "$@"; }
org=$(api_curl -H 'content-type: application/json' -d '{"name":"Milestone 05 isolated test"}' "$RINGFOUR_API_URL/v1/organizations" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
robot=$(api_curl -H 'content-type: application/json' -d '{"name":"test-robot","profile_ref":null}' "$RINGFOUR_API_URL/v1/organizations/$org/robots" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
enrollment=$(api_curl -X POST "$RINGFOUR_API_URL/v1/robots/$robot/enrollment-tokens" | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')
docker run -d --name "$runner" --network "$network" -v "$stage:/staged:ro" -v "$runner_data:/data" -e RUNNER_AUTH_TOKEN="$secret" ringfour-runner:05-test >/dev/null
[ -z "$(docker port "$runner")" ]
[ "$(docker exec "$runner" id -u)" != 0 ]
[ "$(docker inspect "$runner" --format '{{range .Mounts}}{{if eq .Destination "/staged"}}{{.RW}}{{end}}{{end}}')" = false ]
docker run -d --name "$daemon" --network "$network" -p 127.0.0.1:18086:8081 -v "$stage:/staged" -v "$daemon_data:/data" \
  -e "RUNNER_URL=http://$runner:8082" -e RUNNER_AUTH_TOKEN="$secret" -e "PLATFORM_API_URL=http://$api:8080" ringfour-daemon:05-test >/dev/null
for service in "$api" "$daemon" "$runner"; do
  if docker inspect "$service" --format '{{range .Mounts}}{{println .Destination}}{{end}}' | grep -q docker.sock; then
    echo "$service must not mount a container-engine socket" >&2; exit 1
  fi
done
for _ in $(seq 1 60); do curl -sf 127.0.0.1:18086/health/ready >/dev/null && break; sleep 1; done
curl -fsS -H 'content-type: application/json' -d "{\"token\":\"$enrollment\"}" 127.0.0.1:18086/v1/enroll >/dev/null
for _ in $(seq 1 60); do
  online=$(api_curl "$RINGFOUR_API_URL/v1/robots/$robot/status" | python3 -c 'import json,sys; print(json.load(sys.stdin)["online"])')
  [ "$online" = True ] && break
  sleep 1
done
[ "$online" = True ] || { echo 'test daemon did not connect' >&2; exit 1; }
diagnostic=$("${cli[@]}" robot fixture-start "$robot" hello | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
for _ in $(seq 1 60); do
  operation=$(api_curl "$RINGFOUR_API_URL/v1/robots/$robot/operations/$diagnostic")
  state=$(python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])' <<<"$operation")
  [ "$state" = succeeded ] && break
  [ "$state" != failed ] || { echo '04 diagnostic start failed' >&2; exit 1; }
  sleep 1
done
[ "$state" = succeeded ] || { echo '04 diagnostic start timed out' >&2; exit 1; }
for _ in $(seq 1 60); do
  inspect=$("${cli[@]}" robot fixture-inspect "$robot" "$diagnostic" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
  for _ in $(seq 1 30); do
    operation=$(api_curl "$RINGFOUR_API_URL/v1/robots/$robot/operations/$inspect")
    state=$(python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])' <<<"$operation")
    [ "$state" = succeeded ] && break
    [ "$state" != failed ] || { echo '04 diagnostic inspection failed' >&2; exit 1; }
    sleep 1
  done
  completed=$(python3 -c 'import json,sys; x=json.load(sys.stdin); print(x.get("result",{}).get("execution",{}).get("state", ""))' <<<"$operation")
  [ "$completed" = succeeded ] && break
  [ "$completed" != failed ] || { echo '04 diagnostic execution failed' >&2; exit 1; }
  sleep 1
done
python3 -c 'import base64,json,sys; x=json.load(sys.stdin)["result"]; assert x["execution"]["state"]=="succeeded" and x["execution"]["exit_code"]==0 and b"Hello from Ringfour Runner" in base64.b64decode(x["output"]["data_base64"])' <<<"$operation"
echo 'PASS: milestone 04 diagnostic Runner fixture regression'
uv run --project apps/cli python - "$org" "$test_dir" <<'PY'
import json, pathlib, sys
from ringfour_cli.script_bundle import build_script_bundle
folder = pathlib.Path(sys.argv[2])
bundle = build_script_bundle(pathlib.Path("tests/fixtures/scripts/hello.py"))
(folder / "interrupted.zip").write_bytes(bundle.archive)
(folder / "revision.json").write_text(json.dumps(bundle.revision_request(sys.argv[1], ">=3.13,<3.14")))
PY
revision=$(api_curl -H 'content-type: application/json' -H 'Idempotency-Key: interrupted-upload' --data-binary @"$test_dir/revision.json" "$RINGFOUR_API_URL/v1/script-revisions" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
set +e
curl -sS --max-time 1 --limit-rate 1 -o /dev/null -H "authorization: Bearer $access_token" \
  -H 'content-type: application/zip' --upload-file "$test_dir/interrupted.zip" \
  "$RINGFOUR_API_URL/v1/script-revisions/$revision/content" 2>"$test_dir/interrupted-error"
interrupt_result=$?
set -e
[ "$interrupt_result" = 28 ] || { echo "expected an interrupted upload, curl returned $interrupt_result" >&2; exit 1; }
finalize_status=$(curl -sS -o /dev/null -w '%{http_code}' -H "authorization: Bearer $access_token" -X POST "$RINGFOUR_API_URL/v1/script-revisions/$revision/finalize")
[ "$finalize_status" = 409 ] || { echo "partial upload finalized unexpectedly: $finalize_status" >&2; exit 1; }
api_curl -X PUT -H 'content-type: application/zip' --data-binary @"$test_dir/interrupted.zip" "$RINGFOUR_API_URL/v1/script-revisions/$revision/content" >/dev/null
api_curl -X POST "$RINGFOUR_API_URL/v1/script-revisions/$revision/finalize" | python3 -c 'import json,sys; assert json.load(sys.stdin)["state"]=="ready"'
echo 'PASS: interrupted upload cannot finalize; full retry succeeds'
if [ "${RINGFOUR_TEST_STORAGE:-filesystem}" = garage ]; then
  outage_revision=$(api_curl -H 'content-type: application/json' -H 'Idempotency-Key: storage-outage' --data-binary @"$test_dir/revision.json" "$RINGFOUR_API_URL/v1/script-revisions" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
  docker stop "$garage" >/dev/null
  outage_status=$(curl -sS --max-time 30 -o /dev/null -w '%{http_code}' -H "authorization: Bearer $access_token" -X PUT \
    --data-binary @"$test_dir/interrupted.zip" "$RINGFOUR_API_URL/v1/script-revisions/$outage_revision/content")
  [ "$outage_status" = 503 ] || { echo "Garage outage returned $outage_status, expected 503" >&2; exit 1; }
  readiness_status=$(curl -sS --max-time 30 -o /dev/null -w '%{http_code}' "$RINGFOUR_API_URL/health/ready")
  [ "$readiness_status" = 503 ] || { echo "Garage outage readiness returned $readiness_status, expected 503" >&2; exit 1; }
  docker start "$garage" >/dev/null
  for _ in $(seq 1 60); do docker exec "$garage" /garage bucket info ringfour-test >/dev/null 2>&1 && break; sleep 1; done
  docker exec "$garage" /garage bucket info ringfour-test >/dev/null 2>&1
  api_curl -X PUT --data-binary @"$test_dir/interrupted.zip" "$RINGFOUR_API_URL/v1/script-revisions/$outage_revision/content" >/dev/null
  api_curl -X POST "$RINGFOUR_API_URL/v1/script-revisions/$outage_revision/finalize" | python3 -c 'import json,sys; assert json.load(sys.stdin)["state"]=="ready"'
  echo 'PASS: Garage outage returns unavailable and upload retry succeeds'
fi
"${cli[@]}" run tests/fixtures/scripts/hello.py --robot "$robot" --org "$org" --idempotency-key integration-hello >"$test_dir/out" 2>"$test_dir/err"
grep -qx 'hello from uploaded script' "$test_dir/out"
grep -qx 'script stderr' "$test_dir/err"
execution=$(grep -oE '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}' "$test_dir/err" | tail -1)
"${cli[@]}" artifact download "$execution" result.txt -o "$test_dir/result.txt" >/dev/null
printf 'ringfour artifact\n' >"$test_dir/expected"
cmp "$test_dir/result.txt" "$test_dir/expected"
api_curl "$RINGFOUR_API_URL/v1/executions/$execution" | python3 -c 'import json,sys; x=json.load(sys.stdin); assert x["state"]=="succeeded" and x["exit_code"]==0 and x["actor_user_id"]'
"${cli[@]}" run tests/fixtures/scripts/hello.py --robot "$robot" --org "$org" --idempotency-key integration-hello --detach >"$test_dir/retry-out" 2>"$test_dir/retry-err"
grep -q "$execution" "$test_dir/retry-err"
echo 'PASS: CLI upload, stdout, stderr, actor, artifact and idempotent replay'
set +e
"${cli[@]}" run tests/fixtures/scripts/fail.py --robot "$robot" --org "$org" >"$test_dir/fail-out" 2>"$test_dir/fail-err"
result=$?
set -e
[ "$result" = 7 ] && grep -q 'intentional failure' "$test_dir/fail-err"
echo 'PASS: failing script returns exit code 7'
"${cli[@]}" run tests/fixtures/scripts/locked.py --lock tests/fixtures/scripts/six.lock --robot "$robot" --org "$org" >"$test_dir/locked-out" 2>"$test_dir/locked-err"
grep -qx 'six 1.17.0' "$test_dir/locked-out"
set +e
"${cli[@]}" run tests/fixtures/scripts/locked.py --lock tests/fixtures/scripts/six-invalid.lock --robot "$robot" --org "$org" >"$test_dir/bad-lock-out" 2>"$test_dir/bad-lock-err"
bad_lock_result=$?
set -e
[ "$bad_lock_result" = 1 ] || { echo "bad lock returned $bad_lock_result instead of 1" >&2; exit 1; }
bad_lock=$(grep -oE '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}' "$test_dir/bad-lock-err" | tail -1)
api_curl "$RINGFOUR_API_URL/v1/executions/$bad_lock" | python3 -c 'import json,sys; x=json.load(sys.stdin); assert x["state"]=="failed" and x["error"]["code"]=="preparation_failed"'
echo 'PASS: hash-pinned dependencies install; incorrect dependency hash fails preparation'
"${cli[@]}" run tests/fixtures/scripts/long.py --robot "$robot" --org "$org" --detach >"$test_dir/long-out" 2>"$test_dir/long-err"
long=$(grep -oE '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}' "$test_dir/long-err" | tail -1)
for _ in $(seq 1 60); do
  state=$(api_curl "$RINGFOUR_API_URL/v1/executions/$long" | python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])')
  [ "$state" = running ] && break
  sleep 1
done
[ "$state" = running ] || { echo "long execution reached $state" >&2; exit 1; }
"${cli[@]}" execution stop "$long" >/dev/null
for _ in $(seq 1 60); do
  state=$(api_curl "$RINGFOUR_API_URL/v1/executions/$long" | python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])')
  [ "$state" = cancelled ] && break
  sleep 1
done
[ "$state" = cancelled ] || { echo "stop left execution in $state" >&2; exit 1; }
echo 'PASS: hosted stop cancels running script'
"${cli[@]}" run tests/fixtures/scripts/stream.py --robot "$robot" --org "$org" --detach >"$test_dir/stream-out" 2>"$test_dir/stream-err"
stream=$(grep -oE '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}' "$test_dir/stream-err" | tail -1)
for _ in $(seq 1 30); do
  output=$(api_curl "$RINGFOUR_API_URL/v1/executions/$stream/output?stream=stdout&offset=0&limit=16384")
  has_data=$(python3 -c 'import json,sys; print(bool(json.load(sys.stdin)["data_base64"]))' <<<"$output")
  [ "$has_data" = True ] && break
  sleep 1
done
[ "$has_data" = True ] || { echo 'stream produced no hosted output' >&2; exit 1; }
docker restart "$daemon" >/dev/null
for _ in $(seq 1 60); do
  state=$(api_curl "$RINGFOUR_API_URL/v1/executions/$stream" | python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])')
  [ "$state" = succeeded ] && break
  [ "$state" != failed ] || { echo 'stream failed during daemon restart' >&2; exit 1; }
  sleep 1
done
[ "$state" = succeeded ] || { echo "stream left execution in $state" >&2; exit 1; }
"${cli[@]}" logs "$stream" >"$test_dir/stream-actual"
python3 -c 'import sys; print("".join(f"line {i:02}\n" for i in range(12)), end="")' >"$test_dir/stream-expected"
cmp "$test_dir/stream-actual" "$test_dir/stream-expected"
echo 'PASS: daemon reconnect resumes output without gaps or duplicates'
"${cli[@]}" run tests/fixtures/scripts/stream.py --robot "$robot" --org "$org" --detach >"$test_dir/api-restart-out" 2>"$test_dir/api-restart-err"
api_stream=$(grep -oE '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}' "$test_dir/api-restart-err" | tail -1)
for _ in $(seq 1 30); do
  output=$(api_curl "$RINGFOUR_API_URL/v1/executions/$api_stream/output?stream=stdout&offset=0&limit=16384")
  has_data=$(python3 -c 'import json,sys; print(bool(json.load(sys.stdin)["data_base64"]))' <<<"$output")
  [ "$has_data" = True ] && break
  sleep 1
done
[ "$has_data" = True ] || { echo 'API restart stream produced no hosted output' >&2; exit 1; }
old_session=$(docker exec "$postgres" psql -At -U ringfour -d ringfour -c "SELECT session_id FROM robot_presence WHERE robot_id='$robot'")
docker restart "$api" >/dev/null
for _ in $(seq 1 60); do curl -sf "$RINGFOUR_API_URL/health/ready" >/dev/null && break; sleep 1; done
for _ in $(seq 1 60); do
  state=$(api_curl "$RINGFOUR_API_URL/v1/executions/$api_stream" | python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])')
  [ "$state" = succeeded ] && break
  [ "$state" != failed ] || { echo 'stream failed during API restart' >&2; exit 1; }
  sleep 1
done
[ "$state" = succeeded ] || { echo "API restart left execution in $state" >&2; exit 1; }
"${cli[@]}" logs "$api_stream" >"$test_dir/api-stream-actual"
cmp "$test_dir/api-stream-actual" "$test_dir/stream-expected"
echo 'PASS: API restart preserves execution and resumes output transfer'
for _ in $(seq 1 60); do
  new_session=$(docker exec "$postgres" psql -At -U ringfour -d ringfour -c "SELECT session_id FROM robot_presence WHERE robot_id='$robot'")
  [ -n "$new_session" ] && [ "$new_session" != "$old_session" ] && break
  sleep 1
done
[ -n "$new_session" ] && [ "$new_session" != "$old_session" ] || { echo 'daemon did not reconnect after API restart' >&2; exit 1; }
"${cli[@]}" run tests/fixtures/scripts/long.py --robot "$robot" --org "$org" --detach >"$test_dir/restart-out" 2>"$test_dir/restart-err"
interrupted=$(grep -oE '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}' "$test_dir/restart-err" | tail -1)
for _ in $(seq 1 60); do
  state=$(api_curl "$RINGFOUR_API_URL/v1/executions/$interrupted" | python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])')
  [ "$state" = running ] && break
  sleep 1
done
[ "$state" = running ] || { echo "restart test reached $state" >&2; exit 1; }
docker restart "$runner" >/dev/null
for _ in $(seq 1 60); do
  body=$(api_curl "$RINGFOUR_API_URL/v1/executions/$interrupted")
  state=$(python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])' <<<"$body")
  [ "$state" = failed ] && break
  sleep 1
done
python3 -c 'import json,sys; x=json.load(sys.stdin); assert x["state"]=="failed" and x["error"]["code"]=="interrupted_unknown_outcome"' <<<"$body"
echo 'PASS: Runner restart reports an interrupted unknown outcome without replay'
# A second actual API process uses a temporary test-only signer to exercise a
# different human subject against the same disposable PostgreSQL database.
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out "$test_dir/test-signing.pem" 2>/dev/null
docker run -d --name "$test_api" --network "$network" -p 127.0.0.1:18087:8080 -v "$api_data:/storage" \
  -e "ConnectionStrings__Postgres=Host=$postgres;Port=5432;Database=ringfour;Username=ringfour;Password=temporary-test-password" \
  -e ASPNETCORE_ENVIRONMENT=Development -e Auth__Test__Issuer=ringfour-e2e -e Auth__Test__Audience=ringfour-e2e \
  -e "Auth__Test__RsaPrivateKeyPem=$(cat "$test_dir/test-signing.pem")" \
  "${storage_args[@]}" ringfour-api:05-test >/dev/null
for _ in $(seq 1 60); do curl -sf 127.0.0.1:18087/health/ready >/dev/null && break; sleep 1; done
outsider=$(python3 - "$test_dir/test-signing.pem" <<'PY'
import base64, json, subprocess, sys, time
def encode(value):
    return base64.urlsafe_b64encode(value).rstrip(b"=")
header = encode(json.dumps({"alg":"RS256","typ":"JWT","kid":"ringfour-test-key"}, separators=(",", ":")).encode())
claims = encode(json.dumps({"iss":"ringfour-e2e","aud":"ringfour-e2e","sub":"test-outsider", "exp":int(time.time())+300}, separators=(",", ":")).encode())
content = header + b"." + claims
signature = subprocess.run(["openssl", "dgst", "-sha256", "-sign", sys.argv[1]], input=content, check=True, stdout=subprocess.PIPE).stdout
print((content + b"." + encode(signature)).decode())
PY
)
curl -fsS -H "authorization: Bearer $outsider" -H 'content-type: application/json' \
  -d '{"name":"Other isolated organization"}' http://127.0.0.1:18087/v1/organizations >/dev/null
scope_status=$(curl -sS -o "$test_dir/outsider.json" -w '%{http_code}' -H "authorization: Bearer $outsider" "http://127.0.0.1:18087/v1/executions/$execution")
[ "$scope_status" = 404 ] || { echo "cross-organization access returned $scope_status" >&2; exit 1; }
python3 -c 'import json,sys; assert json.load(open(sys.argv[1]))["error"]["code"]=="not_found"' "$test_dir/outsider.json"
echo 'PASS: another authenticated subject cannot read the execution (test-only signing key)'
