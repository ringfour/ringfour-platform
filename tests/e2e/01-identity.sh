#!/usr/bin/env bash
# Milestone 01 integrated acceptance: identity over the real stack.
# The Auth0 device-authorization step (ringfour login) requires a human to
# approve in the browser and is run manually; this script covers everything
# automatable and asserts the CLI state it produces.
set -euo pipefail
cd "$(dirname "$0")/../.."

COMPOSE="docker compose -f infra/compose/docker-compose.yml"
fail() { echo "FAIL: $1" >&2; exit 1; }

echo "==> stack readiness"
curl -sf localhost:8080/health/ready | grep -q '"status":"ok"' || fail "api not ready"
curl -sf localhost:8081/health/ready | grep -q '"status":"ok"' || fail "daemon not ready"

echo "==> unauthenticated /v1/me returns 401 error envelope"
status=$(curl -s -o /tmp/rf-01-unauth.json -w "%{http_code}" localhost:8080/v1/me)
[ "$status" = "401" ] || fail "expected 401, got $status"
grep -q '"code":"unauthenticated"' /tmp/rf-01-unauth.json || fail "401 body missing unauthenticated envelope"

echo "==> garbage bearer token returns 401"
status=$(curl -s -o /dev/null -w "%{http_code}" -H "Authorization: Bearer garbage" localhost:8080/v1/me)
[ "$status" = "401" ] || fail "expected 401 for garbage token, got $status"

echo "==> CLI identity (requires a prior manual 'ringfour login' with approval)"
cd apps/cli
set -a
# shellcheck disable=SC1091
. ../../.auth.env
set +a
export RINGFOUR_AUTH0_DOMAIN="$AUTH0_DOMAIN"
export RINGFOUR_AUTH0_CLIENT_ID="$AUTH0_CLI_CLIENT_ID"
export RINGFOUR_AUTH0_AUDIENCE="$AUTH0_AUDIENCE"

first=$(uv run ringfour whoami)
second=$(uv run ringfour whoami)
[ "$first" = "$second" ] || fail "whoami not stable across calls"
echo "$first"

echo "==> org round-trip"
org_name="e2e-01-$(date +%s)"
uv run ringfour org create "$org_name" || fail "org create failed"
uv run ringfour org list | grep -q "$org_name" || fail "created org missing from list"

echo "PASS: milestone 01 automatable acceptance checks"
