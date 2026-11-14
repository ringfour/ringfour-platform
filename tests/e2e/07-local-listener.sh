#!/usr/bin/env bash
# Isolated real daemon HTTPS listener; provision a test grant locally.
set -euo pipefail
cd "$(dirname "$0")/../.."
name="rf07-local-$$"
cleanup() { docker rm -f "$name" >/dev/null 2>&1 || true; }
trap cleanup EXIT
docker build -q -t ringfour-daemon:07-local-test apps/daemon >/dev/null
docker run -d --name "$name" -p 127.0.0.1::8084 -e RINGFOUR_LOCAL_DEV_ENABLED=true -e PLATFORM_API_URL=http://127.0.0.1:9 ringfour-daemon:07-local-test >/dev/null
port=$(docker port "$name" 8084/tcp | sed 's/.*://')
for _ in $(seq 1 60); do docker exec "$name" test -f /data/local-dev-tls.pem && break; sleep 1; done
uv run --project apps/daemon python - "$name" "$port" <<'PY'
import base64, datetime, hashlib, json, secrets, socket, ssl, subprocess, sys, uuid
name, port = sys.argv[1], int(sys.argv[2])
raw = secrets.token_bytes(32)
token = base64.urlsafe_b64encode(raw).decode().rstrip('=')
now = datetime.datetime.now(datetime.UTC)
stamp = lambda seconds: (now + datetime.timedelta(seconds=seconds)).isoformat().replace('+00:00', 'Z')
session = str(uuid.uuid4())
body = {'operation_id': str(uuid.uuid4()), 'session_id': session, 'grant_id': str(uuid.uuid4()), 'token_sha256': hashlib.sha256(raw).hexdigest(), 'expires_at': stamp(120), 'scopes': ['source', 'output']}
program = '''import json, sys
from ringfour_daemon.local_grants import install, record_session
from ringfour_daemon.local_identity import local_identity
v=json.load(sys.stdin)
record_session('/data/ringfour.db',v['body']['session_id'],v['expiry'],'create')
install('/data/ringfour.db',v['body'])
print(local_identity('/data/local-dev-tls.pem'))'''
proc = subprocess.run(['docker','exec','-i',name,'/app/.venv/bin/python','-c',program], input=json.dumps({'body':body,'expiry':stamp(3600)}), text=True, check=True, capture_output=True)
fingerprint = proc.stdout.strip()
def fetch(bearer):
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE
    with socket.create_connection(('127.0.0.1', port), timeout=5) as raw_socket:
        with context.wrap_socket(raw_socket, server_hostname='localhost') as peer:
            assert hashlib.sha256(peer.getpeercert(binary_form=True)).hexdigest() == fingerprint
            peer.sendall(f'GET /v1/local/dev-sessions/{session} HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer {bearer}\r\nConnection: close\r\n\r\n'.encode())
            response = b''
            while chunk := peer.recv(4096): response += chunk
            return response
assert b' 401 ' in fetch('wrong')
response = fetch(token)
assert b' 200 ' in response and session.encode() in response and token.encode() not in response
print('PASS: actual daemon HTTPS listener accepted a pinned, scoped grant and rejected an invalid grant')
PY
