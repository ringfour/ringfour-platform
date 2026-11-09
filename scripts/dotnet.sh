#!/usr/bin/env bash
# Run the pinned .NET SDK inside Docker; no host SDK installation required.
# NuGet packages and dotnet tools (dotnet-ef) persist in named volumes.
set -euo pipefail
cd "$(dirname "$0")/.."

# Keep build artifacts on native Linux volumes: container-written files on the
# macOS bind mount hit stale handles and bogus timestamps (Docker Desktop).
# The repo root is mounted (not just apps/api) so tests can read the shared
# contracts under packages/contracts via repo-relative paths.
exec docker run --rm \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e DOTNET_NOLOGO=1 \
  -e PATH=/root/.dotnet/tools:/usr/share/dotnet:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
  -v "$PWD":/repo \
  -v ringfour-dotnet-nuget:/root/.nuget/packages \
  -v ringfour-dotnet-tools:/root/.dotnet \
  -v ringfour-api-obj:/repo/apps/api/src/Ringfour.Api/obj \
  -v ringfour-api-bin:/repo/apps/api/src/Ringfour.Api/bin \
  -v ringfour-api-tests-obj:/repo/apps/api/tests/Ringfour.Api.Tests/obj \
  -v ringfour-api-tests-bin:/repo/apps/api/tests/Ringfour.Api.Tests/bin \
  -w /repo/apps/api \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet "$@"
