#!/bin/sh
set -eu

# Run under the robot owner's maintenance gate, which must hold the robot idle
# through the entire update. Use the same compose project, file and env as boot.
: "${RINGFOUR_ROBOT_COMPOSE_FILE:?Set the enrolled robot Compose file}"
: "${RINGFOUR_ROBOT_ENV:?Set the enrolled robot Compose environment file}"
: "${RINGFOUR_ROBOT_PROJECT:?Set the enrolled robot Compose project name}"
: "${RINGFOUR_DAEMON_READY_URL:?Set the private Daemon readiness URL}"
: "${RINGFOUR_ROBOT_MAINTENANCE:?Set the operator's maintenance wrapper executable}"

if [ "${1:-}" != --maintenance-held ]; then
  exec "$RINGFOUR_ROBOT_MAINTENANCE" "$0" --maintenance-held
fi

exec 9>"${RINGFOUR_ROBOT_ENV}.update.lock"
flock -n 9 || exit 0

repo=https://github.com/ringfour/ringfour-platform
release_url=$(curl -fsSL -o /dev/null -w '%{url_effective}' "$repo/releases/latest")
tag=${release_url##*/}
printf '%s\n' "$tag" | grep -Eq '^main-[0-9]+-[0-9]+$' || {
  echo "No Ringfour main release found" >&2
  exit 1
}
assets="$repo/releases/download/$tag"

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
curl -fsSL "$assets/SHA256SUMS" -o "$tmp/SHA256SUMS"
curl -fsSL "$assets/daemon-image.txt" -o "$tmp/daemon-image.txt"
(
  cd "$tmp"
  awk '$2 == "daemon-image.txt"' SHA256SUMS > daemon.sha256
  sha256sum -c daemon.sha256
)
image=$(cat "$tmp/daemon-image.txt")
printf '%s\n' "$image" | grep -Eq '^ghcr\.io/ringfour/ringfour-platform-daemon@sha256:[a-f0-9]{64}$'

compose() {
  docker compose -p "$RINGFOUR_ROBOT_PROJECT" --env-file "$RINGFOUR_ROBOT_ENV" --env-file "$pin" \
    -f "$RINGFOUR_ROBOT_COMPOSE_FILE" "$@"
}
service=${RINGFOUR_DAEMON_SERVICE:-daemon}
pin=${RINGFOUR_ROBOT_IMAGE_PIN:-"${RINGFOUR_ROBOT_ENV}.image"}
[ -r "$pin" ] || { echo "Initialize the robot image pin at $pin" >&2; exit 1; }
id=$(compose ps -a -q "$service")
[ -n "$id" ] || { echo "Start the enrolled Daemon before enabling automatic updates" >&2; exit 1; }
previous=$(docker inspect --format '{{.Config.Image}}' "$id")
if [ "$previous" = "$image" ] && grep -Fxq "RINGFOUR_DAEMON_IMAGE=$image" "$pin" &&
    curl -fsS --max-time 5 "$RINGFOUR_DAEMON_READY_URL" >/dev/null; then
  exit 0
fi
# A mutable previous reference cannot support a repeatable rollback.
printf '%s\n' "$previous" | grep -Eq '@sha256:[a-f0-9]{64}$' || {
  echo "Set an initial digest-pinned Daemon image before enabling updates" >&2
  exit 1
}
export RINGFOUR_DAEMON_IMAGE="$image"
resolved=$(compose config --format json | jq -r --arg service "$service" '.services[$service].image')
[ "$resolved" = "$image" ] || { echo 'Compose must use RINGFOUR_DAEMON_IMAGE for the Daemon service' >&2; exit 1; }
compose pull "$service"
if compose up -d --no-deps --no-build "$service" &&
    curl -fsS --max-time 5 --retry 15 --retry-delay 2 --retry-all-errors "$RINGFOUR_DAEMON_READY_URL" >/dev/null; then
  pending_pin=$(mktemp "${pin}.XXXXXX")
  printf 'RINGFOUR_DAEMON_IMAGE=%s\n' "$image" > "$pending_pin"
  mv -f "$pending_pin" "$pin"
  printf 'Updated Daemon to %s\n' "$image"
else
  echo "Daemon failed readiness. The last successful image pin is in $pin. Check SQLite migration compatibility before rollback." >&2
  exit 1
fi
