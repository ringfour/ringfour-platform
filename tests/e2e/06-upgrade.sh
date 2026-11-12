#!/usr/bin/env bash
# Upgrade a disposable PostgreSQL database from the final 05 migration.
set -euo pipefail
cd "$(dirname "$0")/../.."
name="rf06-migration-$$"
cleanup() { docker rm -f "$name" >/dev/null 2>&1 || true; }
trap cleanup EXIT
docker run -d --name "$name" -p 127.0.0.1::5432 -e POSTGRES_DB=ringfour -e POSTGRES_USER=ringfour -e POSTGRES_PASSWORD=test-only postgres:18 >/dev/null
for _ in $(seq 1 60); do docker exec "$name" pg_isready -U ringfour -d ringfour >/dev/null 2>&1 && break; sleep 1; done
port=$(docker port "$name" 5432/tcp | sed 's/.*://')
export ConnectionStrings__Postgres="Host=127.0.0.1;Port=$port;Database=ringfour;Username=ringfour;Password=test-only"
export PATH="${DOTNET_ROOT:-/tmp/ringfour-dotnet-sdk-10}:$PATH"
export DOTNET_ROOT="${DOTNET_ROOT:-/tmp/ringfour-dotnet-sdk-10}"
(
  cd apps/api
  dotnet ef database update 20261112164004_ScriptUploadCleanup --project src/Ringfour.Api >/dev/null
)
docker exec -i "$name" psql -v ON_ERROR_STOP=1 -U ringfour -d ringfour >/dev/null <<'SQL'
INSERT INTO organizations (id,name,created_at) VALUES ('00000000-0000-0000-0000-000000000001','prior org',now());
INSERT INTO users (id,auth0_sub,created_at) VALUES ('00000000-0000-0000-0000-000000000002','auth0|prior',now());
INSERT INTO script_revisions (id,organization_id,created_by_user_id,state,archive_sha256,archive_size,revision_digest,entrypoint,python_requirement,lock_sha256,idempotency_key,request_fingerprint,created_at,upload_expires_at,finalized_at)
VALUES ('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000002','ready',repeat('a',64),10,repeat('b',64),'main.py','>=3.13',repeat('c',64),'prior-key',repeat('d',64),now(),now(),now());
SQL
(
  cd apps/api
  dotnet ef database update --project src/Ringfour.Api >/dev/null
)
count=$(docker exec "$name" psql -At -U ringfour -d ringfour -c "SELECT count(*) FROM script_revisions WHERE id='00000000-0000-0000-0000-000000000003' AND state='ready' AND idempotency_key='prior-key'")
[ "$count" = 1 ]
columns=$(docker exec "$name" psql -At -U ringfour -d ringfour -c "SELECT count(*) FROM information_schema.columns WHERE table_name='script_executions' AND column_name IN ('deployment_id','mission_revision_id')")
[ "$columns" = 2 ]
echo 'PASS: milestone 05 database upgrades to 06 and preserves prior ready script revision'
