#!/usr/bin/env bash
# Real Agent, daemon, API and PostgreSQL. The Agent event is synthetic; no ROS or robot.
set -euo pipefail
cd "$(dirname "$0")/../.."
name="rf08-motion-$$"
network="$name-net"
api="$name-api"
postgres="$name-postgres"
daemon="$name-daemon"
agent="$name-agent"
volume="$name-shared"
cleanup() {
  rm -f "/tmp/rf08-motion-identifiers-$agent"
  docker rm -f "$agent" "$daemon" "$api" "$postgres" >/dev/null 2>&1 || true
  docker volume rm "$volume" >/dev/null 2>&1 || true
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
docker volume create "$volume" >/dev/null
docker run -d --name "$postgres" --network "$network" -e POSTGRES_DB=ringfour -e POSTGRES_USER=ringfour -e POSTGRES_PASSWORD=temporary-test-password postgres:18 >/dev/null
for _ in $(seq 1 60); do docker exec "$postgres" pg_isready -U ringfour -d ringfour >/dev/null 2>&1 && break; sleep 1; done
docker run -d --name "$api" --network "$network" -p 127.0.0.1::8080 \
  -e "ConnectionStrings__Postgres=Host=$postgres;Port=5432;Database=ringfour;Username=ringfour;Password=temporary-test-password" \
  -e "Auth0__Domain=$AUTH0_DOMAIN" -e "Auth0__Audience=$AUTH0_AUDIENCE" -e Storage__Backend=filesystem -e Storage__FilesystemRoot=/storage ringfour-api:08-test >/dev/null
docker run -d --name "$daemon" --network "$network" -p 127.0.0.1::8081 \
  --mount "source=$volume,target=/run/ringfour-motion,readonly" -e "PLATFORM_API_URL=http://$api:8080" \
  -e RINGFOUR_AGENT_MOTION_SOCKET=/run/ringfour-motion/motion.sock ringfour-daemon:08-test >/dev/null
api_port=$(docker port "$api" 8080/tcp | sed 's/.*://')
daemon_port=$(docker port "$daemon" 8081/tcp | sed 's/.*://')
export RINGFOUR_API_URL="http://127.0.0.1:$api_port" RF_TEST_POSTGRES="$postgres" RF_TEST_API="$api" RF_TEST_DAEMON="$daemon" RF_TEST_DAEMON_PORT="$daemon_port" RF_TEST_AGENT="$agent"
for _ in $(seq 1 60); do curl -sf "$RINGFOUR_API_URL/health/ready" >/dev/null && curl -sf "http://127.0.0.1:$daemon_port/health/live" >/dev/null && break; sleep 1; done
curl -fsS "$RINGFOUR_API_URL/health/ready" >/dev/null
curl -fsS "http://127.0.0.1:$daemon_port/health/live" >/dev/null

uv run --project apps/cli python - <<'PY'
import os, subprocess
import httpx
from ringfour_cli.auth import TokenStore
from ringfour_cli.config import load_settings
token = TokenStore().load(load_settings())['access_token']
client = httpx.Client(base_url=os.environ['RINGFOUR_API_URL'], timeout=10, headers={'authorization': f'Bearer {token}'})
def post(path, body):
    response = client.post(path, json=body); response.raise_for_status(); return response.json()
org = post('/v1/organizations', {'name':'Motion logbook isolated test'})['id']
robot = post(f'/v1/organizations/{org}/robots', {'name':'test robot','profile_ref':None})['id']
enroll = post(f'/v1/robots/{robot}/enrollment-tokens', None)['token']
response = httpx.post(f"http://127.0.0.1:{os.environ['RF_TEST_DAEMON_PORT']}/v1/enroll",json={'token':enroll},timeout=10)
response.raise_for_status()
subprocess.run(['docker','stop',os.environ['RF_TEST_API']],check=True,stdout=subprocess.DEVNULL)
with open('/tmp/rf08-motion-identifiers-' + os.environ['RF_TEST_AGENT'], 'w') as out:
    out.write(org + '\n' + robot + '\n')
PY

docker run -d --name "$agent" --network "$network" --mount "source=$volume,target=/run/ringfour-motion" \
  -v "$PWD/../ringfour-testing/platform_tests/fixtures:/profiles:ro" \
  -v "$PWD/tests/e2e/08-motion-agent.py:/motion-agent.py:ro" \
  ringfour-agent:c-preview python3 /motion-agent.py >/dev/null

uv run --project apps/cli python - <<'PY'
import os, subprocess, time
import httpx
from ringfour_cli.auth import TokenStore
from ringfour_cli.config import load_settings
agent, daemon, api = (os.environ['RF_TEST_AGENT'], os.environ['RF_TEST_DAEMON'], os.environ['RF_TEST_API'])
path = '/tmp/rf08-motion-identifiers-' + agent
with open(path) as inp: org, robot = inp.read().splitlines()
os.unlink(path)
query = 'import sqlite3; c=sqlite3.connect("/data/ringfour.db"); print(c.execute("select count(*) from logbook_outbox").fetchone()[0])'
for _ in range(60):
    running = subprocess.check_output(['docker','inspect','-f','{{.State.Running}}',agent],text=True).strip()
    if running != 'true': raise AssertionError('Agent failed to start: ' + subprocess.check_output(['docker','logs',agent],text=True))
    queued = subprocess.check_output(['docker','exec',daemon,'/app/.venv/bin/python','-c',query],text=True).strip()
    if queued == '3': break
    time.sleep(1)
else: raise AssertionError('motion event did not reach daemon outbox')
subprocess.run(['docker','restart',daemon],check=True,stdout=subprocess.DEVNULL)
subprocess.run(['docker','start',api],check=True,stdout=subprocess.DEVNULL)
for _ in range(90):
    count = subprocess.check_output(['docker','exec',os.environ['RF_TEST_POSTGRES'],'psql','-At','-U','ringfour','-d','ringfour','-c',f"SELECT count(*) FROM logbook_events WHERE robot_id='{robot}' AND type='agent.motion.event'"],text=True).strip()
    queued = subprocess.check_output(['docker','exec',daemon,'/app/.venv/bin/python','-c',query],text=True).strip()
    if count == '3' and queued == '0': break
    time.sleep(1)
else: raise AssertionError(f'motion did not replicate: hosted={count}, queued={queued}')
token = TokenStore().load(load_settings())['access_token']
api_port = subprocess.check_output(['docker','port',api,'8080/tcp'],text=True).strip().rsplit(':',1)[1]
response = httpx.get(f'http://127.0.0.1:{api_port}/v1/organizations/{org}/events',headers={'authorization': f'Bearer {token}'},params={'type':'agent.motion.event'},timeout=10)
response.raise_for_status()
items = response.json()['items']
assert [item['payload']['event_type'] for item in items] == ['motion.accepted', 'motion.stop_verified', 'motion.completed']
assert items[1]['payload']['stop_verified'] is True
assert all(item['operation_id'] is None and item['execution_id'] is None and item['actor_user_id'] is None for item in items)
print('PASS: synthetic Agent task lifecycle reached daemon and Platform exactly once after offline restart')
PY
