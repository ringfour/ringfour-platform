#!/usr/bin/env bash
# Milestone 02 integrated acceptance: robot enrollment over the real stack.
# Prerequisites: `ringfour-cli login` approved for the owner account (account 1)
# and the compose stack running. Cross-account isolation is verified manually
# with account 2 (see docs/human-testing/02.md); everything else is asserted
# here against real processes — no mocks.
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

echo "==> device endpoints reject garbage without any human token"
status=$(curl -s -o /tmp/rf-02-badtoken.json -w "%{http_code}" \
  -H 'content-type: application/json' \
  -d '{"token":"rft_garbage"}' localhost:8080/v1/device/enroll)
[ "$status" = "401" ] || fail "expected 401 for garbage token, got $status"
grep -q '"code":"invalid_token"' /tmp/rf-02-badtoken.json || fail "expected invalid_token envelope"

status=$(curl -s -o /dev/null -w "%{http_code}" \
  -H 'content-type: application/json' \
  -d '{"credential_id":"00000000-0000-0000-0000-000000000000","secret":"rfs_garbage"}' \
  localhost:8080/v1/device/authenticate)
[ "$status" = "401" ] || fail "expected 401 for unknown credential, got $status"

cd apps/cli
set -a
# shellcheck disable=SC1091
. ../../.auth.env
set +a
export RINGFOUR_AUTH0_DOMAIN="$AUTH0_DOMAIN"
export RINGFOUR_AUTH0_CLIENT_ID="$AUTH0_CLI_CLIENT_ID"
export RINGFOUR_AUTH0_AUDIENCE="$AUTH0_AUDIENCE"

echo "==> owner creates an organization and a robot"
org_name="e2e-02-$(date +%s)"
uv run ringfour-cli org create "$org_name" >/dev/null || fail "org create failed"
org_id=$(uv run ringfour-cli org list | grep "$org_name" | grep -oE '[0-9a-f-]{36}' | head -1)
[ -n "$org_id" ] || fail "could not parse org id"
uv run ringfour-cli org select "$org_id" >/dev/null || fail "org select failed"

create_out=$(uv run ringfour-cli robot create "e2e-bot-$(date +%s)")
robot_id=$(echo "$create_out" | grep -oE '[0-9a-f-]{36}' | head -1)
[ -n "$robot_id" ] || fail "could not parse robot id"
show_out=$(uv run ringfour-cli robot show "$robot_id")
grep -qE "Enrolled: +no" <<<"$show_out" || fail "new robot must not be enrolled"
echo "robot: $robot_id"

echo "==> owner issues an enrollment token"
token_out=$(uv run ringfour-cli robot enrollment-token "$robot_id")
token=$(echo "$token_out" | grep -oE 'rft_[A-Za-z0-9_-]{43}')
[ -n "$token" ] || fail "could not parse enrollment token"

echo "==> daemon enrolls through the CLI"
uv run ringfour-cli daemon enroll "$token" || fail "daemon enroll failed"
daemon_view=$(uv run ringfour-cli daemon identity)
grep -q "$robot_id" <<<"$daemon_view" || fail "daemon identity missing robot"

echo "==> identity survives a daemon restart"
$COMPOSE restart daemon >/dev/null
for _ in $(seq 1 30); do
  sleep 1
  curl -sf localhost:8081/health/ready >/dev/null 2>&1 && break
done
curl -sf localhost:8081/health/ready | grep -q '"status":"ok"' || fail "daemon not ready after restart"
identity=$(uv run ringfour-cli daemon identity)
echo "$identity" | grep -q "$robot_id" || fail "identity lost after restart"
echo "$identity" | grep -q "$org_id" || fail "identity org mismatch after restart"

echo "==> consumed token cannot be reused (platform-level)"
status=$(curl -s -o /tmp/rf-02-reuse.json -w "%{http_code}" \
  -H 'content-type: application/json' \
  -d "{\"token\":\"$token\"}" localhost:8080/v1/device/enroll)
[ "$status" = "401" ] || fail "expected 401 on token reuse, got $status"
grep -q '"code":"invalid_token"' /tmp/rf-02-reuse.json || fail "reuse returned wrong error code"

echo "==> rotation: old credential keeps working inside the overlap"
old_secret=$($COMPOSE exec -T daemon cat /data/robot-credential) || fail "daemon credential file unreadable"
# The credentials table truncates UUIDs at terminal width; the daemon identity
# view prints the full credential id.
identity_view=$(uv run ringfour-cli daemon identity)
old_credential=$(grep "Credential:" <<<"$identity_view" | grep -oE '[0-9a-f-]{36}')
[ -n "$old_credential" ] || fail "could not parse current credential id"

rotate_out=$(uv run ringfour-cli robot rotate "$robot_id")
new_secret=$(echo "$rotate_out" | grep -oE 'rfs_[A-Za-z0-9_-]{43}')
new_credential=$(echo "$rotate_out" | grep -oE '[0-9a-f-]{36}' | head -1)
[ -n "$new_secret" ] || fail "could not parse rotated secret"
[ "$new_credential" != "$old_credential" ] || fail "rotation did not create a new credential"

status=$(curl -s -o /dev/null -w "%{http_code}" \
  -H 'content-type: application/json' \
  -d "{\"credential_id\":\"$old_credential\",\"secret\":\"$old_secret\"}" \
  localhost:8080/v1/device/authenticate)
[ "$status" = "200" ] || fail "old credential should still authenticate during overlap, got $status"
status=$(curl -s -o /dev/null -w "%{http_code}" \
  -H 'content-type: application/json' \
  -d "{\"credential_id\":\"$new_credential\",\"secret\":\"$new_secret\"}" \
  localhost:8080/v1/device/authenticate)
[ "$status" = "200" ] || fail "new credential must authenticate, got $status"

echo "==> revocation rejects immediately"
uv run ringfour-cli robot revoke "$robot_id" "$old_credential" || fail "revoke failed"
status=$(curl -s -o /tmp/rf-02-revoked.json -w "%{http_code}" \
  -H 'content-type: application/json' \
  -d "{\"credential_id\":\"$old_credential\",\"secret\":\"$old_secret\"}" \
  localhost:8080/v1/device/authenticate)
[ "$status" = "401" ] || fail "expected 401 for revoked credential, got $status"
grep -q '"code":"invalid_token"' /tmp/rf-02-revoked.json || fail "revoke returned wrong error code"
status=$(curl -s -o /dev/null -w "%{http_code}" \
  -H 'content-type: application/json' \
  -d "{\"credential_id\":\"$new_credential\",\"secret\":\"$new_secret\"}" \
  localhost:8080/v1/device/authenticate)
[ "$status" = "200" ] || fail "surviving credential must keep working, got $status"

echo "PASS: milestone 02 automatable acceptance checks"
