#!/usr/bin/env bash
# Real daemon outbox and API over HTTP; test inserts synthetic operation events.
set -euo pipefail
cd "$(dirname "$0")/../.."
name="rf08-outbox-$$"
network="$name-net"
api="$name-api"
postgres="$name-postgres"
daemon="$name-daemon"
cleanup() {
  docker rm -f "$daemon" "$api" "$postgres" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
}
trap cleanup EXIT
set -a
. ./.auth.env
set +a
export RINGFOUR_AUTH0_DOMAIN="$AUTH0_DOMAIN" RINGFOUR_AUTH0_CLIENT_ID="$AUTH0_CLI_CLIENT_ID" RINGFOUR_AUTH0_AUDIENCE="$AUTH0_AUDIENCE"
docker build -q -t ringfour-api:08-test apps/api >/dev/null
docker build -q -t ringfour-daemon:08-test apps/daemon >/dev/null
docker network create "$network" >/dev/null
docker run -d --name "$postgres" --network "$network" -e POSTGRES_DB=ringfour -e POSTGRES_USER=ringfour -e POSTGRES_PASSWORD=temporary-test-password postgres:18 >/dev/null
for _ in $(seq 1 60); do docker exec "$postgres" pg_isready -U ringfour -d ringfour >/dev/null 2>&1 && break; sleep 1; done
docker run -d --name "$api" --network "$network" -p 127.0.0.1::8080 \
  -e "ConnectionStrings__Postgres=Host=$postgres;Port=5432;Database=ringfour;Username=ringfour;Password=temporary-test-password" \
  -e "Auth0__Domain=$AUTH0_DOMAIN" -e "Auth0__Audience=$AUTH0_AUDIENCE" -e Storage__Backend=filesystem -e Storage__FilesystemRoot=/storage ringfour-api:08-test >/dev/null
docker run -d --name "$daemon" --network "$network" -p 127.0.0.1::8081 -e "PLATFORM_API_URL=http://$api:8080" ringfour-daemon:08-test >/dev/null
api_port=$(docker port "$api" 8080/tcp | sed 's/.*://')
daemon_port=$(docker port "$daemon" 8081/tcp | sed 's/.*://')
export RINGFOUR_API_URL="http://127.0.0.1:$api_port" RF_TEST_POSTGRES="$postgres" RF_TEST_API="$api" RF_TEST_DAEMON="$daemon" RF_TEST_DAEMON_PORT="$daemon_port"
for _ in $(seq 1 60); do curl -sf "$RINGFOUR_API_URL/health/ready" >/dev/null && curl -sf "http://127.0.0.1:$daemon_port/health/live" >/dev/null && break; sleep 1; done
curl -fsS "$RINGFOUR_API_URL/health/ready" >/dev/null
curl -fsS "http://127.0.0.1:$daemon_port/health/live" >/dev/null
uv run --project apps/cli python - <<'PY'
import os, subprocess, time, uuid
import httpx
from ringfour_cli.auth import TokenStore
from ringfour_cli.config import load_settings
base = os.environ['RINGFOUR_API_URL']
token = TokenStore().load(load_settings())['access_token']
client = httpx.Client(base_url=base, timeout=10)
headers = {'authorization': f'Bearer {token}'}
def post(path, body):
    result = client.post(path, json=body, headers=headers); result.raise_for_status(); return result.json()
org = post('/v1/organizations', {'name':'Logbook outbox isolated test'})['id']
robot = post(f'/v1/organizations/{org}/robots', {'name':'test robot','profile_ref':None})['id']
enroll = post(f'/v1/robots/{robot}/enrollment-tokens', None)['token']
response = httpx.post(f"http://127.0.0.1:{os.environ['RF_TEST_DAEMON_PORT']}/v1/enroll",json={'token':enroll},timeout=10)
response.raise_for_status(); assert response.json()['robot_id'] == robot
operation = str(uuid.uuid4())
sql = f"INSERT INTO operations (id,robot_id,kind,payload_version,payload,state,created_at,updated_at,deadline_at) VALUES ('{operation}','{robot}','fixture.test',1,'{{}}','Queued',now(),now(),now()+interval '1 hour')"
subprocess.run(['docker','exec',os.environ['RF_TEST_POSTGRES'],'psql','-q','-U','ringfour','-d','ringfour','-c',sql],check=True,stdout=subprocess.DEVNULL)
subprocess.run(['docker','stop',os.environ['RF_TEST_API']],check=True,stdout=subprocess.DEVNULL)
code = '''import sqlite3,sys
from ringfour_daemon.logbook import append
with sqlite3.connect('/data/ringfour.db') as conn:
 conn.execute('BEGIN IMMEDIATE')
 append(conn,sys.argv[1],sys.argv[2],'accepted')
 append(conn,sys.argv[1],sys.argv[2],'running')
'''
subprocess.run(['docker','exec',os.environ['RF_TEST_DAEMON'],'/app/.venv/bin/python','-c',code,robot,operation],check=True)
subprocess.run(['docker','restart',os.environ['RF_TEST_DAEMON']],check=True,stdout=subprocess.DEVNULL)
query = 'import sqlite3; c=sqlite3.connect("/data/ringfour.db"); print(c.execute("select count(*) from logbook_outbox").fetchone()[0])'
for _ in range(60):
    try:
        count = subprocess.check_output(['docker','exec',os.environ['RF_TEST_DAEMON'],'/app/.venv/bin/python','-c',query],text=True,stderr=subprocess.DEVNULL).strip()
        if count == '2': break
    except subprocess.CalledProcessError: pass
    time.sleep(1)
else: raise AssertionError('outbox did not persist while offline')
subprocess.run(['docker','start',os.environ['RF_TEST_API']],check=True,stdout=subprocess.DEVNULL)
for _ in range(90):
    count = subprocess.check_output(['docker','exec',os.environ['RF_TEST_POSTGRES'],'psql','-At','-U','ringfour','-d','ringfour','-c',f"SELECT count(*) FROM logbook_events WHERE robot_id='{robot}'"],text=True).strip()
    queued = subprocess.check_output(['docker','exec',os.environ['RF_TEST_DAEMON'],'/app/.venv/bin/python','-c',query],text=True).strip()
    if count == '2' and queued == '0': break
    time.sleep(1)
else: raise AssertionError(f'events did not replicate: hosted={count}, queued={queued}')
print('PASS: durable daemon outbox survived offline restart and replicated exactly once')
PY
