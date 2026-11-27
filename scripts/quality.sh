#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if (( $# )); then
  echo 'Usage: bash scripts/quality.sh' >&2
  exit 2
fi
bash scripts/lint.sh
bash scripts/typecheck.sh
bash scripts/test-component.sh
