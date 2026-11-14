#!/usr/bin/env bash
# Isolated API and PostgreSQL; actual HTTP and Auth0, no daemon event producer yet.
set -euo pipefail
cd "$(dirname "$0")/../.."
name="rf08-ingest-$$"
network="$name-net"
api="$name-api"
postgres="$name-postgres"
cleanup() {
  docker rm -f "$api" "$postgres" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
}
trap cleanup EXIT
set -a
. ./.auth.env
set +a
export RINGFOUR_AUTH0_DOMAIN="$AUTH0_DOMAIN" RINGFOUR_AUTH0_CLIENT_ID="$AUTH0_CLI_CLIENT_ID" RINGFOUR_AUTH0_AUDIENCE="$AUTH0_AUDIENCE"
docker build -q -t ringfour-api:08-test apps/api >/dev/null
docker network create "$network" >/dev/null
docker run -d --name "$postgres" --network "$network" -e POSTGRES_DB=ringfour -e POSTGRES_USER=ringfour -e POSTGRES_PASSWORD=temporary-test-password postgres:18 >/dev/null
for _ in $(seq 1 60); do docker exec "$postgres" pg_isready -U ringfour -d ringfour >/dev/null 2>&1 && break; sleep 1; done
docker run -d --name "$api" --network "$network" -p 127.0.0.1::8080 \
  -e "ConnectionStrings__Postgres=Host=$postgres;Port=5432;Database=ringfour;Username=ringfour;Password=temporary-test-password" \
  -e "Auth0__Domain=$AUTH0_DOMAIN" -e "Auth0__Audience=$AUTH0_AUDIENCE" -e Storage__Backend=filesystem -e Storage__FilesystemRoot=/storage ringfour-api:08-test >/dev/null
port=$(docker port "$api" 8080/tcp | sed 's/.*://')
export RINGFOUR_API_URL="http://127.0.0.1:$port" RF_TEST_POSTGRES="$postgres" RF_TEST_API="$api"
for _ in $(seq 1 60); do curl -sf "$RINGFOUR_API_URL/health/ready" >/dev/null && break; sleep 1; done
curl -fsS "$RINGFOUR_API_URL/health/ready" >/dev/null
uv run --project apps/cli python - <<'PY'
import os, subprocess, time, uuid
import httpx
from ringfour_cli.auth import TokenStore
from ringfour_cli.config import load_settings
base = os.environ['RINGFOUR_API_URL']
token = TokenStore().load(load_settings())['access_token']
client = httpx.Client(base_url=base, timeout=10)
human = {'authorization': f'Bearer {token}'}
def post(path, body, headers=human):
    result = client.post(path, json=body, headers=headers); result.raise_for_status(); return result.json()
org = post('/v1/organizations', {'name':'Logbook isolated test'})['id']
robot = post(f'/v1/organizations/{org}/robots', {'name':'test robot','profile_ref':None})['id']
issued = post(f'/v1/robots/{robot}/enrollment-tokens', None)['token']
identity = post('/v1/device/enroll', {'token':issued}, {})
device = {'x-ringfour-credential-id':identity['credential_id'],'x-ringfour-credential-secret':identity['credential_secret']}
operation = str(uuid.uuid4())
sql = f"INSERT INTO operations (id,robot_id,kind,payload_version,payload,state,created_at,updated_at,deadline_at) VALUES ('{operation}','{robot}','fixture.test',1,'{{}}','Queued',now(),now(),now()+interval '1 hour')"
subprocess.run(['docker','exec',os.environ['RF_TEST_POSTGRES'],'psql','-q','-U','ringfour','-d','ringfour','-c',sql],check=True,stdout=subprocess.DEVNULL)
def event(seq,state):
    return {'event_id':str(uuid.uuid4()),'robot_id':robot,'local_sequence':seq,'occurred_at':'2026-11-13T12:00:00Z','type':'device.operation.state_changed','schema_version':1,'operation_id':operation,'payload':{'state':state}}
first, second, third = event(1,'accepted'), event(2,'running'), event(3,'succeeded')
url='/v1/device/logbook/events'
assert client.post(url,json={'events':[first]}).status_code == 401
assert client.post(url,json={'events':[second]},headers=device).status_code == 409
assert post(url,{'events':[first,second]},device)['acknowledged_sequence'] == 2
subprocess.run(['docker','restart',os.environ['RF_TEST_API']],check=True,stdout=subprocess.DEVNULL)
new_port = subprocess.check_output(['docker','port',os.environ['RF_TEST_API'],'8080/tcp'],text=True).strip().rsplit(':',1)[1]
client.close(); client = httpx.Client(base_url=f'http://127.0.0.1:{new_port}',timeout=10)
for _ in range(60):
    try:
        if client.get('/health/ready').status_code == 200: break
    except httpx.HTTPError: pass
    time.sleep(1)
else: raise AssertionError('API did not restart')
assert post(url,{'events':[first,second]},device)['acknowledged_sequence'] == 2
assert post(url,{'events':[second,third]},device)['acknowledged_sequence'] == 3
history = client.get(f'/v1/organizations/{org}/events', headers=human, params={'robot_id':robot,'limit':2})
history.raise_for_status(); first_page = history.json()
assert len(first_page['items']) == 2 and first_page['next_cursor']
history = client.get(f'/v1/organizations/{org}/events', headers=human, params={'robot_id':robot,'limit':2,'cursor':first_page['next_cursor']})
history.raise_for_status(); assert len(history.json()['items']) == 1
assert client.get(f'/v1/organizations/{org}/events').status_code == 401
conflict = {**first,'payload':{'state':'failed'}}
assert client.post(url,json={'events':[conflict]},headers=device).status_code == 409
count = subprocess.check_output(['docker','exec',os.environ['RF_TEST_POSTGRES'],'psql','-At','-U','ringfour','-d','ringfour','-c',f"SELECT count(*) FROM logbook_events WHERE robot_id='{robot}'"],text=True).strip()
assert count == '3', count
print('PASS: actual HTTP ingestion, gap rejection and replay after API restart')
PY
