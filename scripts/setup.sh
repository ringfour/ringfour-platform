#!/usr/bin/env bash
# Install all host-side dependencies for lint/typecheck/component tests.
# Runtime services run in Docker (see npm run compose:up); the CLI is a host tool.
# The .NET SDK runs in Docker via scripts/dotnet.sh; no host SDK is required.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "==> packages/contracts"
npm --prefix packages/contracts ci

echo "==> apps/api (dotnet via Docker)"
bash scripts/dotnet.sh tool restore
bash scripts/dotnet.sh restore

for app in console; do
  if [ -d "apps/$app" ]; then
    echo "==> apps/$app"
    npm --prefix "apps/$app" ci
  fi
done

for app in daemon runner cli; do
  if [ -d "apps/$app" ]; then
    echo "==> apps/$app"
    uv sync --frozen --project "apps/$app"
  fi
done

echo "setup complete"
