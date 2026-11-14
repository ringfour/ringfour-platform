#!/usr/bin/env bash
# Upgrade a disposable database from the last milestone 06 migration.
set -euo pipefail
cd "$(dirname "$0")/../.."
name="rf07-migration-$$"
cleanup() { docker rm -f "$name" >/dev/null 2>&1 || true; }
trap cleanup EXIT
docker run -d --name "$name" -p 127.0.0.1::5432 -e POSTGRES_DB=ringfour -e POSTGRES_USER=ringfour -e POSTGRES_PASSWORD=test-only postgres:18 >/dev/null
for _ in $(seq 1 60); do docker exec "$name" pg_isready -U ringfour -d ringfour >/dev/null 2>&1 && break; sleep 1; done
port=$(docker port "$name" 5432/tcp | sed 's/.*://')
export ConnectionStrings__Postgres="Host=127.0.0.1;Port=$port;Database=ringfour;Username=ringfour;Password=test-only"
export PATH="${DOTNET_ROOT:-/tmp/ringfour-dotnet-sdk-10}:$PATH"
export DOTNET_ROOT="${DOTNET_ROOT:-/tmp/ringfour-dotnet-sdk-10}"
(cd apps/api && dotnet ef database update 20261112174603_MissionExecutionAttribution --project src/Ringfour.Api >/dev/null)
docker exec -i "$name" psql -v ON_ERROR_STOP=1 -U ringfour -d ringfour >/dev/null <<'SQL'
INSERT INTO organizations (id,name,created_at) VALUES ('00000000-0000-0000-0000-000000000001','prior org',now());
INSERT INTO users (id,auth0_sub,created_at) VALUES ('00000000-0000-0000-0000-000000000002','auth0|prior',now());
INSERT INTO robots (id,organization_id,name,created_at) VALUES ('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000001','prior robot',now());
INSERT INTO mission_deployments (id,robot_id,desired_state,observed_state,created_at,updated_at) VALUES ('00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-000000000003','stopped','ready',now(),now());
SQL
(cd apps/api && dotnet ef database update 20261113093829_DevSessions --project src/Ringfour.Api >/dev/null)
docker exec -i "$name" psql -v ON_ERROR_STOP=1 -U ringfour -d ringfour >/dev/null <<'SQL'
INSERT INTO dev_sessions (id,robot_id,owner_user_id,state,generation,created_at,updated_at,expires_at)
VALUES ('00000000-0000-0000-0000-000000000005','00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000002','ready',0,now(),now(),now()+interval '1 hour');
INSERT INTO operations (id,robot_id,kind,payload_version,payload,state,created_at,updated_at,deadline_at)
VALUES ('00000000-0000-0000-0000-000000000006','00000000-0000-0000-0000-000000000003','dev.session.sync',1,'{}','Queued',now(),now(),now()+interval '1 hour');
INSERT INTO dev_session_generations (id,session_id,operation_id,generation,state,manifest,archive_sha256,archive_size,object_key,created_at)
VALUES ('00000000-0000-0000-0000-000000000007','00000000-0000-0000-0000-000000000005','00000000-0000-0000-0000-000000000006',1,'pending','{}',repeat('a',64),1,'legacy',now());
SQL
(cd apps/api && dotnet ef database update --project src/Ringfour.Api >/dev/null)
metadata=$(docker exec "$name" psql -At -U ringfour -d ringfour -c "SELECT count(*) FROM dev_session_generations WHERE id='00000000-0000-0000-0000-000000000007' AND archive_sha256=repeat('a',64)")
[ "$metadata" = 1 ]
legacy=$(docker exec "$name" psql -At -U ringfour -d ringfour -c "SELECT count(*) FROM information_schema.columns WHERE table_schema='public' AND table_name='dev_session_generations' AND column_name IN ('object_key','manifest')")
[ "$legacy" = 0 ]
count=$(docker exec "$name" psql -At -U ringfour -d ringfour -c "SELECT count(*) FROM mission_deployments WHERE id='00000000-0000-0000-0000-000000000004' AND desired_state='stopped'")
[ "$count" = 1 ]
tables=$(docker exec "$name" psql -At -U ringfour -d ringfour -c "SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_name IN ('dev_sessions','dev_session_generations')")
[ "$tables" = 2 ]
logbook=$(docker exec "$name" psql -At -U ringfour -d ringfour -c "SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_name IN ('logbook_events','logbook_checkpoints')")
[ "$logbook" = 2 ]
echo 'PASS: 06 and initial 07 databases upgrade; mission and generation metadata preserved; Logbook tables created'
