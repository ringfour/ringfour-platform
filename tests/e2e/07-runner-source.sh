#!/usr/bin/env bash
# Isolated real Runner source sync over its private HTTP protocol.
set -euo pipefail
cd "$(dirname "$0")/../.."
name="rf07-source-$$"
dir=$(mktemp -d "${TMPDIR:-/tmp}/ringfour-source.XXXXXXXX")
cleanup() { docker rm -f "$name" >/dev/null 2>&1 || true; rm -rf "$dir"; }
trap cleanup EXIT
mkdir -p "$dir/staged"
chmod 755 "$dir" "$dir/staged"
docker build -q -t ringfour-runner:07-test apps/runner >/dev/null
docker run -d --name "$name" -p 127.0.0.1::8082 -v "$dir/staged:/staged:ro" -e RUNNER_AUTH_TOKEN=local-test-secret ringfour-runner:07-test >/dev/null
port=$(docker port "$name" 8082/tcp | sed 's/.*://')
for _ in $(seq 1 60); do curl -sf "http://127.0.0.1:$port/health/ready" >/dev/null && break; sleep 1; done
uv run --project apps/runner python - "$name" "$port" "$dir/staged" <<'PY'
import hashlib, io, pathlib, subprocess, sys, time, uuid, zipfile
import httpx
name, port, stage = sys.argv[1], int(sys.argv[2]), pathlib.Path(sys.argv[3])
session = str(uuid.uuid4())
create = {'session_id':session,'operation_id':str(uuid.uuid4()),'reason':'create','expires_at':'2099-01-01T00:00:00Z'}
files = {'main.py':b'print("local generation")\n','requirements.lock':b''}
out = io.BytesIO()
with zipfile.ZipFile(out, 'w') as archive:
    for path, data in files.items(): archive.writestr(path, data)
raw = out.getvalue(); (stage/'snapshot.zip').write_bytes(raw)
hash_bytes = lambda data: hashlib.sha256(data).hexdigest()
body = {'session_id':session,'operation_id':str(uuid.uuid4()),'base_generation':0,'generation':1,'archive_sha256':hash_bytes(raw),'archive_size':len(raw),'staged_archive':'snapshot.zip','manifest':{'entrypoint':'main.py','python_requirement':'>=3.13,<3.14','lock_sha256':hash_bytes(b''),'files':[{'path':path,'size':len(data),'sha256':hash_bytes(data)} for path,data in files.items()],'deleted_paths':[]}}
with httpx.Client(base_url=f'http://127.0.0.1:{port}',headers={'Authorization':'Bearer local-test-secret'},timeout=10) as client:
    client.post('/v1/dev-sessions/lifecycle',json=create).raise_for_status()
    response=client.post('/v1/dev-sessions/sync',json=body); response.raise_for_status()
    assert response.json()['generation']==1
    stale=client.post('/v1/dev-sessions/sync',json={**body,'operation_id':str(uuid.uuid4())}); assert stale.status_code==409
subprocess.run(['docker','restart',name],check=True,stdout=subprocess.DEVNULL)
port = int(subprocess.check_output(['docker','port',name,'8082/tcp'], text=True).strip().rsplit(':', 1)[1])
for _ in range(60):
    try:
        if httpx.get(f'http://127.0.0.1:{port}/health/ready', timeout=1).status_code == 200: break
    except httpx.HTTPError: pass
    time.sleep(0.5)
else: raise RuntimeError('Runner did not become ready')
with httpx.Client(base_url=f'http://127.0.0.1:{port}',headers={'Authorization':'Bearer local-test-secret'},timeout=20) as client:
    response=client.post('/v1/dev-sessions/sync',json=body); response.raise_for_status()
    assert response.json()['generation']==1
    close={**create,'operation_id':str(uuid.uuid4()),'reason':'close'}
    client.post('/v1/dev-sessions/lifecycle',json=close).raise_for_status()
print('PASS: real Runner committed, replayed after restart, and cleaned up a source generation')
PY
