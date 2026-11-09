#!/usr/bin/env bash
# Run every app's component tests independently. Integration lives in tests/e2e/.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "==> test packages/contracts"
npm --prefix packages/contracts test

echo "==> test apps/api (xUnit + Testcontainers via Docker)"
# Testcontainers spawns real PostgreSQL sibling containers, so the SDK
# container gets the host Docker socket (dev tooling only; the runtime
# stack never mounts it) and reaches published ports via host.docker.internal.
docker run --rm \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e DOTNET_NOLOGO=1 \
  -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal \
  -v "$PWD":/repo \
  -v ringfour-dotnet-nuget:/root/.nuget/packages \
  -v ringfour-api-obj:/repo/apps/api/src/Ringfour.Api/obj \
  -v ringfour-api-bin:/repo/apps/api/src/Ringfour.Api/bin \
  -v ringfour-api-tests-obj:/repo/apps/api/tests/Ringfour.Api.Tests/obj \
  -v ringfour-api-tests-bin:/repo/apps/api/tests/Ringfour.Api.Tests/bin \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -w /repo/apps/api \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet test

for app in console; do
  if [ -d "apps/$app" ]; then
    echo "==> test apps/$app"
    npm --prefix "apps/$app" test
  fi
done

for app in daemon runner cli; do
  if [ -d "apps/$app" ]; then
    echo "==> test apps/$app"
    (cd "apps/$app" && uv run pytest)
  fi
done
