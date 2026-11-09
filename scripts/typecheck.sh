#!/usr/bin/env bash
# Type-check apps. The C# compiler (via Docker) is the API type check.
# Python apps are covered by ruff (lint) and pytest.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "==> typecheck apps/api (dotnet build via Docker)"
bash scripts/dotnet.sh build --no-restore

for app in console; do
  if [ -d "apps/$app" ]; then
    echo "==> typecheck apps/$app"
    npm --prefix "apps/$app" run typecheck
  fi
done
