#!/usr/bin/env bash
# Isolated actual Platform, PostgreSQL, daemon and local TLS listener.
# Exercises real session creation and closure without a physical robot.
set -euo pipefail
cd "$(dirname "$0")/../.."
name="rf07-grant-$$"
network="$name-net"
api="$name-api"
postgres="$name-postgres"
daemon="$name-daemon"
runner="$name-runner"
stage="$name-stage"
runner_data="$name-runner-data"
daemon_data="$name-daemon-data"
api_data="$name-api-data"
cleanup() {
  docker rm -f "$daemon" "$runner" "$api" "$postgres" >/dev/null 2>&1 || true
  docker volume rm "$stage" "$runner_data" "$daemon_data" "$api_data" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
}
trap cleanup EXIT
set -a
. ./.auth.env
set +a
export RINGFOUR_AUTH0_DOMAIN="$AUTH0_DOMAIN" RINGFOUR_AUTH0_CLIENT_ID="$AUTH0_CLI_CLIENT_ID" RINGFOUR_AUTH0_AUDIENCE="$AUTH0_AUDIENCE"
export RINGFOUR_API_URL="http://127.0.0.1:18095"
cli=(uv run --project "$PWD/apps/cli" ringfour-cli)
secret=$(openssl rand -hex 32)
docker build -q -t ringfour-api:07-test apps/api >/dev/null
docker build -q -t ringfour-daemon:07-test apps/daemon >/dev/null
docker build -q -t ringfour-runner:07-test apps/runner >/dev/null
docker network create "$network" >/dev/null
for v in "$stage" "$runner_data" "$daemon_data" "$api_data"; do docker volume create "$v" >/dev/null; done
docker run -d --name "$postgres" --network "$network" -e POSTGRES_DB=ringfour -e POSTGRES_USER=ringfour -e POSTGRES_PASSWORD=temporary-test-password postgres:18 >/dev/null
for _ in $(seq 1 60); do docker exec "$postgres" pg_isready -U ringfour -d ringfour >/dev/null 2>&1 && break; sleep 1; done
docker run -d --name "$api" --network "$network" -p 127.0.0.1:18095:8080 -v "$api_data:/storage" \
  -e "ConnectionStrings__Postgres=Host=$postgres;Port=5432;Database=ringfour;Username=ringfour;Password=temporary-test-password" \
  -e "Auth0__Domain=$AUTH0_DOMAIN" -e "Auth0__Audience=$AUTH0_AUDIENCE" -e Storage__Backend=filesystem -e Storage__FilesystemRoot=/storage ringfour-api:07-test >/dev/null
for _ in $(seq 1 60); do curl -sf "$RINGFOUR_API_URL/health/ready" >/dev/null && break; sleep 1; done
curl -fsS "$RINGFOUR_API_URL/health/ready" >/dev/null
"${cli[@]}" whoami >/dev/null
access_token=$(uv run --project apps/cli python -c 'from ringfour_cli.auth import TokenStore; from ringfour_cli.config import load_settings; print(TokenStore().load(load_settings())["access_token"])')
api_curl() { curl -fsS -H "authorization: Bearer $access_token" "$@"; }
org=$(api_curl -H 'content-type: application/json' -d '{"name":"Milestone 07 isolated test"}' "$RINGFOUR_API_URL/v1/organizations" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
robot=$(api_curl -H 'content-type: application/json' -d '{"name":"test-robot","profile_ref":null}' "$RINGFOUR_API_URL/v1/organizations/$org/robots" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
owner=$(api_curl "$RINGFOUR_API_URL/v1/me" | python3 -c 'import json,sys; print(json.load(sys.stdin)["user"]["id"])')
enrollment=$(api_curl -X POST "$RINGFOUR_API_URL/v1/robots/$robot/enrollment-tokens" | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')
docker run -d --name "$runner" --network "$network" -v "$stage:/staged:ro" -v "$runner_data:/data" -e RUNNER_AUTH_TOKEN="$secret" ringfour-runner:07-test >/dev/null
docker run -d --name "$daemon" --network "$network" -p 127.0.0.1::8081 -p 127.0.0.1::8084 -v "$stage:/staged" -v "$daemon_data:/data" \
  -e "RUNNER_URL=http://$runner:8082" -e RUNNER_AUTH_TOKEN="$secret" -e "PLATFORM_API_URL=http://$api:8080" -e RINGFOUR_LOCAL_DEV_ENABLED=true ringfour-daemon:07-test >/dev/null
control_port=$(docker port "$daemon" 8081/tcp | sed 's/.*://')
local_port=$(docker port "$daemon" 8084/tcp | sed 's/.*://')
for _ in $(seq 1 60); do curl -sf "127.0.0.1:$control_port/health/ready" >/dev/null && break; sleep 1; done
curl -fsS -H 'content-type: application/json' -d "{\"token\":\"$enrollment\"}" "127.0.0.1:$control_port/v1/enroll" >/dev/null
for _ in $(seq 1 60); do
  online=$(api_curl "$RINGFOUR_API_URL/v1/robots/$robot/status" | python3 -c 'import json,sys; print(json.load(sys.stdin)["online"])')
  advertised=$(docker exec "$postgres" psql -At -U ringfour -d ringfour -c "SELECT count(*) FROM dev_local_endpoints WHERE robot_id='$robot'")
  [ "$online" = True ] && [ "$advertised" = 1 ] && break
  sleep 1
done
[ "$online" = True ] && [ "$advertised" = 1 ]
create=$(api_curl -H 'content-type: application/json' -H 'Idempotency-Key: local-session-create' -d '{"ttl_seconds":3600}' "$RINGFOUR_API_URL/v1/robots/$robot/dev-sessions")
session=$(python3 -c 'import json,sys;print(json.load(sys.stdin)["session_id"])' <<<"$create")
for _ in $(seq 1 60); do
  session_state=$(api_curl "$RINGFOUR_API_URL/v1/dev-sessions/$session" | python3 -c 'import json,sys;print(json.load(sys.stdin)["state"])')
  [ "$session_state" = ready ] && break
  [ "$session_state" != failed ] || { echo 'session creation failed' >&2; exit 1; }
  sleep 1
done
[ "$session_state" = ready ]
export RF_TEST_ACCESS_TOKEN="$access_token" RF_TEST_SESSION="$session" RF_TEST_ROBOT="$robot" RF_TEST_LOCAL_PORT="$local_port" RF_TEST_DAEMON="$daemon" RF_TEST_API="$api" RF_TEST_POSTGRES="$postgres"
uv run --project apps/daemon python - <<'PY'
import base64, hashlib, io, json, os, secrets, socket, ssl, subprocess, time, uuid, zipfile
import httpx
from cryptography import x509
from datetime import UTC, datetime
raw = secrets.token_bytes(32)
token = base64.urlsafe_b64encode(raw).decode().rstrip('=')
base = os.environ['RINGFOUR_API_URL']; session = os.environ['RF_TEST_SESSION']; robot = os.environ['RF_TEST_ROBOT']
headers = {'Authorization': 'Bearer '+os.environ['RF_TEST_ACCESS_TOKEN'], 'Idempotency-Key': str(uuid.uuid4())}
with httpx.Client(base_url=base, headers=headers, timeout=10) as client:
    response = client.post(f'/v1/dev-sessions/{session}/local-grants', json={'grant_id': str(uuid.uuid4()), 'token_sha256': hashlib.sha256(raw).hexdigest()})
    response.raise_for_status(); grant = response.json()
    for _ in range(60):
        result = client.get(f'/v1/robots/{robot}/operations/{grant["operation_id"]}'); result.raise_for_status()
        state = result.json()['state']
        if state in ('succeeded', 'failed'): break
        time.sleep(1)
    assert state == 'succeeded', state
ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT); ctx.check_hostname = False; ctx.verify_mode = ssl.CERT_NONE
with socket.create_connection(('127.0.0.1', int(os.environ['RF_TEST_LOCAL_PORT'])), timeout=5) as sock:
    with ctx.wrap_socket(sock, server_hostname='localhost') as peer:
        der = peer.getpeercert(binary_form=True)
        assert hashlib.sha256(der).hexdigest() == grant['certificate_sha256']
        cert = x509.load_der_x509_certificate(der); now = datetime.now(UTC)
        assert cert.not_valid_before_utc <= now < cert.not_valid_after_utc
        peer.sendall(f'GET /v1/local/dev-sessions/{session} HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer {token}\r\nConnection: close\r\n\r\n'.encode())
        data = b''
        while chunk := peer.recv(4096): data += chunk
        assert b' 200 ' in data and session.encode() in data and token.encode() not in data
print('PASS: real Platform grant and authenticated daemon fingerprint enabled a pinned local HTTPS request')
# The Platform is unavailable for the upload: bytes flow only over local HTTPS.
subprocess.run(['docker', 'stop', os.environ['RF_TEST_API']], check=True, stdout=subprocess.DEVNULL)
marker = 'LOCAL_SOURCE_' + uuid.uuid4().hex
files = {'main.py': ('print(\"'+marker+'\")\n').encode(), 'requirements.lock': b''}
buffer = io.BytesIO()
with zipfile.ZipFile(buffer, 'w') as archive:
    for path, contents in files.items(): archive.writestr(path, contents)
archive_bytes = buffer.getvalue(); sha = lambda data: hashlib.sha256(data).hexdigest()
source = {'base_generation':0,'archive_sha256':sha(archive_bytes),'archive_size':len(archive_bytes),
          'archive_base64':base64.b64encode(archive_bytes).decode(),
          'manifest':{'entrypoint':'main.py','python_requirement':'>=3.13,<3.14','lock_sha256':sha(b''),
                      'files':[{'path':path,'size':len(contents),'sha256':sha(contents)} for path,contents in files.items()], 'deleted_paths':[]}}
source_bytes = json.dumps(source,separators=(',', ':')).encode()
key = str(uuid.uuid4())
def local_sync(port):
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT); context.check_hostname = False; context.verify_mode = ssl.CERT_NONE
    with socket.create_connection(('127.0.0.1', port),timeout=5) as sock:
        with context.wrap_socket(sock,server_hostname='localhost') as peer:
            der = peer.getpeercert(binary_form=True)
            assert hashlib.sha256(der).hexdigest() == grant['certificate_sha256']
            cert = x509.load_der_x509_certificate(der); now = datetime.now(UTC)
            assert cert.not_valid_before_utc <= now < cert.not_valid_after_utc
            head = (f'PUT /v1/local/dev-sessions/{session}/source HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer {token}\r\nIdempotency-Key: {key}\r\nContent-Type: application/json\r\nContent-Length: {len(source_bytes)}\r\nConnection: close\r\n\r\n').encode()
            peer.sendall(head + source_bytes)
            data=b''
            while chunk := peer.recv(65536): data += chunk
            assert b' 200 ' in data, data[:500]
            return json.loads(data.split(b'\r\n\r\n',1)[1])
first = local_sync(int(os.environ['RF_TEST_LOCAL_PORT']))
assert first['generation'] == 1 and local_sync(int(os.environ['RF_TEST_LOCAL_PORT'])) == first
# Restart daemon while the Platform is still down; the durable report must survive.
subprocess.run(['docker','restart',os.environ['RF_TEST_DAEMON']],check=True,stdout=subprocess.DEVNULL)
port = int(subprocess.check_output(['docker','port',os.environ['RF_TEST_DAEMON'],'8084/tcp'],text=True).strip().rsplit(':',1)[1])
for _ in range(60):
    try:
        assert local_sync(port) == first
        break
    except (OSError, ConnectionError): time.sleep(0.5)
else: raise AssertionError('local sync replay did not recover after daemon restart')
subprocess.run(['docker','start',os.environ['RF_TEST_API']],check=True,stdout=subprocess.DEVNULL)
for _ in range(90):
    try:
        status = httpx.get(f'{base}/v1/dev-sessions/{session}',headers={'Authorization':'Bearer '+os.environ['RF_TEST_ACCESS_TOKEN']},timeout=3)
        if status.status_code == 200 and status.json()['generation'] == 1: break
    except httpx.HTTPError: pass
    time.sleep(1)
else: raise AssertionError('generation report did not recover')
assert status.json()['dependency_digest'] == sha(b'')
sql = "SELECT count(*) FROM operations WHERE payload::text LIKE '%" + marker + "%'"
assert subprocess.check_output(['docker','exec',os.environ['RF_TEST_POSTGRES'],'psql','-At','-U','ringfour','-d','ringfour','-c',sql],text=True).strip() == '0'
print('PASS: LAN source committed with Platform offline; report recovered after daemon restart')
subprocess.run(['docker', 'stop', os.environ['RF_TEST_DAEMON']], check=True, stdout=subprocess.DEVNULL)
close = httpx.delete(f'{base}/v1/dev-sessions/{session}', headers={'Authorization':'Bearer '+os.environ['RF_TEST_ACCESS_TOKEN'], 'Idempotency-Key':str(uuid.uuid4())}, timeout=10)
close.raise_for_status()
status = httpx.get(f'{base}/v1/dev-sessions/{session}', headers={'Authorization':'Bearer '+os.environ['RF_TEST_ACCESS_TOKEN']}, timeout=10).json()
assert status['state'] == 'closing', status
subprocess.run(['docker', 'start', os.environ['RF_TEST_DAEMON']], check=True, stdout=subprocess.DEVNULL)
for _ in range(60):
    status = httpx.get(f'{base}/v1/dev-sessions/{session}', headers={'Authorization':'Bearer '+os.environ['RF_TEST_ACCESS_TOKEN']}, timeout=10).json()
    if status['state'] == 'closed': break
    time.sleep(1)
assert status['state'] == 'closed', status
print('PASS: offline closure remained pending until the daemon reconnected and Runner confirmed cleanup')
PY
