#!/usr/bin/env bash
# Milestone 00 integrated acceptance: real Compose stack, real CLI, real protocols.
# Usage: bash tests/e2e/00-health.sh
set -euo pipefail
cd "$(dirname "$0")/../.."

COMPOSE="docker compose -f infra/compose/docker-compose.yml"
API_URL=http://localhost:8080
DAEMON_URL=http://localhost:8081
RUNNER_PORT=8082

fail() { echo "FAIL: $*" >&2; exit 1; }

wait_http() { # url expected-code timeout-seconds
  local url=$1 expected=$2 deadline=$((SECONDS + $3))
  while [ $SECONDS -lt $deadline ]; do
    code=$(curl -s -o /dev/null -w '%{http_code}' "$url" || true)
    [ "$code" = "$expected" ] && return 0
    sleep 1
  done
  fail "timeout waiting for $url to return $expected (last: ${code:-none})"
}

echo "==> 1. clean start from empty volumes"
$COMPOSE down -v --remove-orphans >/dev/null 2>&1 || true
$COMPOSE up -d --build

wait_http "$API_URL/health/ready" 200 120
wait_http "$DAEMON_URL/health/ready" 200 120
echo "    api and daemon ready"

echo "==> 2. runner is private (no published port)"
code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 "http://localhost:$RUNNER_PORT/health/live" || true)
[ "$code" = "000" ] || fail "runner reachable on localhost:$RUNNER_PORT (code $code) — must stay private"
echo "    runner not reachable from host (connection failed as expected)"

echo "==> 3. CLI health — all services ok"
uv run --project apps/cli ringfour-cli health
echo "    exit code $?"

echo "==> 4. stop postgres — api readiness fails, CLI exits nonzero"
$COMPOSE stop postgres >/dev/null
wait_http "$API_URL/health/ready" 503 30
body=$(curl -s "$API_URL/health/ready")
[ "$(echo "$body" | python3 -c 'import json,sys; print(json.load(sys.stdin)["checks"]["postgresql"])')" = "fail" ] \
  || fail "expected postgresql=fail, got: $body"
if uv run --project apps/cli ringfour-cli health; then
  fail "CLI health exited 0 with postgres down"
else
  echo "    CLI exited nonzero as expected; api: $body"
fi

echo "==> 5. restore postgres — api recovers without deleting volumes"
$COMPOSE start postgres >/dev/null
wait_http "$API_URL/health/ready" 200 60
echo "    api ready again"

echo "==> 6. stop runner — daemon readiness fails, CLI exits nonzero"
$COMPOSE stop runner >/dev/null
wait_http "$DAEMON_URL/health/ready" 503 30
body=$(curl -s "$DAEMON_URL/health/ready")
[ "$(echo "$body" | python3 -c 'import json,sys; print(json.load(sys.stdin)["checks"]["runner"])')" = "fail" ] \
  || fail "expected runner=fail, got: $body"
if uv run --project apps/cli ringfour-cli health; then
  fail "CLI health exited 0 with runner down"
else
  echo "    CLI exited nonzero as expected; daemon: $body"
fi

echo "==> 7. restore runner — daemon recovers"
$COMPOSE start runner >/dev/null
wait_http "$DAEMON_URL/health/ready" 200 60
echo "    daemon ready again"

echo "==> 8. console dev server serves the health page shell"
wait_http http://localhost:5173 200 60
curl -s http://localhost:5173 | grep -q '<div id="root">' || fail "console did not serve index.html"
curl -s http://localhost:5173/api/health/ready | grep -q '"status":"ok"' \
  || fail "console vite proxy to api failed"
echo "    console serves and proxies api health"

echo "==> 9. final state: all ready"
uv run --project apps/cli ringfour-cli health

echo "ACCEPTANCE PASSED — stack left running; clean up with: npm run compose:clean"
