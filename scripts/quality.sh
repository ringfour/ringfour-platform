#!/usr/bin/env bash
# Complete local gate; integration requires a disposable, configured stack.
set -euo pipefail
cd "$(dirname "$0")/.."
if [ "${1:-}" != "" ] && [ "${1:-}" != "--integration" ]; then
  echo 'Usage: bash scripts/quality.sh [--integration]' >&2
  exit 2
fi
bash scripts/lint.sh
bash scripts/typecheck.sh
bash scripts/test-component.sh
if [ "${1:-}" = "--integration" ]; then
  bash tests/e2e/00-health.sh
  bash tests/e2e/01-identity.sh
  bash tests/e2e/02-enrollment.sh
  bash tests/e2e/02-pairing.sh
  bash tests/e2e/03-connectivity.sh
  bash tests/e2e/03-pre-ack.sh
  bash tests/e2e/04-runner.sh
else
  echo 'Integration and external device gates not run; see docs/development.md.'
fi
