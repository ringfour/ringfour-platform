#!/usr/bin/env bash
# Start the local Compose stack from any working directory.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$root"

# Auth0 settings traditionally live in .auth.env; root .env can override them.


# Keep the local database password stable across restarts without requiring a
# committed development credential. An explicitly configured value wins.
if [ -z "${POSTGRES_PASSWORD:-}" ]; then
  password_file="$root/.env.server"
  if [ ! -s "$password_file" ]; then
    generated_password="$(openssl rand -hex 32)"
    if ! (umask 077; set -o noclobber; printf 'POSTGRES_PASSWORD=%s\n' "$generated_password" > "$password_file") 2>/dev/null; then
      if [ ! -s "$password_file" ]; then
        printf 'Could not create %s; set POSTGRES_PASSWORD in .env.\n' "$password_file" >&2
        exit 1
      fi
    fi
  fi
  set -a
  . "$password_file"
  set +a
fi

compose_args=(-f "$root/infra/server/docker-compose.yml")
if [ -f "$root/.env" ]; then
  # The Compose file is nested under infra/server, so specify the app-root
  # environment file explicitly instead of relying on Compose's auto-discovery.
  compose_args+=(--env-file "$root/.env")
fi

exec docker compose "${compose_args[@]}" up --build "$@"
