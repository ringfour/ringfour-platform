#!/usr/bin/env bash
# Lint every app independently.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "==> lint apps/api (dotnet format via Docker)"
bash scripts/dotnet.sh format --verify-no-changes --verbosity minimal

for app in console; do
  if [ -d "apps/$app" ]; then
    echo "==> lint apps/$app"
    npm --prefix "apps/$app" run lint
  fi
done

for app in daemon runner cli; do
  if [ -d "apps/$app" ]; then
    echo "==> lint apps/$app"
    (cd "apps/$app" && uv run ruff check src tests)
  fi
done
