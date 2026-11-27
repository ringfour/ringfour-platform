#!/bin/sh
set -eu

directory=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
config=${RINGFOUR_DEPLOYMENT_ENV:-"$directory/.env"}
if [ ! -r "$config" ]; then
  echo "Create $config using the deployment .env.example before running this script." >&2
  exit 1
fi
exec 9>"$directory/.deployment.lock"
flock -n 9 || { echo "A deployment is already running" >&2; exit 1; }

repo=https://github.com/ringfour/ringfour-platform
if [ "$#" -eq 0 ]; then
  release_url=$(curl -fsSL -o /dev/null -w '%{url_effective}' "$repo/releases/latest")
  tag=${release_url##*/}
elif [ "$#" -eq 1 ]; then
  tag=$1
else
  echo "Usage: $0 [main-release-tag]" >&2
  exit 1
fi
printf '%s\n' "$tag" | grep -Eq '^main-[0-9]+-[0-9]+$' || { echo "Invalid main release tag" >&2; exit 1; }
assets="$repo/releases/download/$tag"
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
curl -fsSL "$assets/SHA256SUMS" -o "$stage/SHA256SUMS"
curl -fsSL "$assets/deployment.tar.gz" -o "$stage/deployment.tar.gz"
(
  cd "$stage"
  awk '$2 == "deployment.tar.gz"' SHA256SUMS > deployment.sha256
  sha256sum -c deployment.sha256
  tar -xzf deployment.tar.gz
)

# Keep image pins authoritative even when running from an interactive shell.
unset RINGFOUR_API_IMAGE RINGFOUR_STUDIO_IMAGE
compose() {
  docker compose -p ringfour-server --env-file "$config" --env-file "$stage/images.env" \
    -f "$stage/compose.yml" "$@"
}

compose config --quiet
compose pull
compose up -d --no-build --wait --wait-timeout 180
studio_address=$(compose port console 80)
curl -fsS --retry 15 --retry-delay 2 --retry-all-errors "http://$studio_address/api/health/ready"
printf '\nRunning Ringfour release %s\n' "$tag"
