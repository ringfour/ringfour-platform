#!/usr/bin/env bash
# Exercise persistent identity in two real daemon containers without starting a listener.
set -euo pipefail
cd "$(dirname "$0")/../.."
volume="rf07-tls-$$"
cleanup() { docker volume rm "$volume" >/dev/null 2>&1 || true; }
trap cleanup EXIT
docker build -q -t ringfour-daemon:07-local-test apps/daemon >/dev/null
docker volume create "$volume" >/dev/null
first=$(docker run --rm -v "$volume:/data" ringfour-daemon:07-local-test /app/.venv/bin/python -c 'from ringfour_daemon.local_identity import local_identity; print(local_identity("/data/local-tls.pem"))')
second=$(docker run --rm -v "$volume:/data" ringfour-daemon:07-local-test /app/.venv/bin/python -c 'from ringfour_daemon.local_identity import local_identity; print(local_identity("/data/local-tls.pem"))')
[ "$first" = "$second" ]
echo 'PASS: daemon TLS identity persists across containers with the same volume'
