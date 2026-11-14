#!/usr/bin/env bash
# Real private Runner: source-only environment reuse and safe restart recovery.
set -euo pipefail
cd "$(dirname "$0")/../.."
name="rf07-restart-$$"
dir=$(mktemp -d "${TMPDIR:-/tmp}/ringfour-restart.XXXXXXXX")
cleanup() { docker rm -f "$name" >/dev/null 2>&1 || true; rm -rf "$dir"; }
trap cleanup EXIT
mkdir -p "$dir/staged"
chmod 755 "$dir" "$dir/staged"
docker build -q -t ringfour-runner:07-test apps/runner >/dev/null
docker run -d --name "$name" -p 127.0.0.1::8082 -v "$dir/staged:/staged:ro" -e RUNNER_AUTH_TOKEN=local-test-secret ringfour-runner:07-test >/dev/null
port=$(docker port "$name" 8082/tcp | sed 's/.*://')
for _ in $(seq 1 60); do curl -sf "http://127.0.0.1:$port/health/ready" >/dev/null && break; sleep 1; done
uv run --project apps/runner python - "$name" "$port" "$dir/staged" <<'PY'
import base64, hashlib, io, pathlib, subprocess, sys, time, uuid, zipfile
import httpx
name, port, stage = sys.argv[1], int(sys.argv[2]), pathlib.Path(sys.argv[3])
session, robot = str(uuid.uuid4()), str(uuid.uuid4())
create = {'session_id':session,'operation_id':str(uuid.uuid4()),'reason':'create','expires_at':'2099-01-01T00:00:00Z'}
headers={'Authorization':'Bearer local-test-secret'}
def request(method,path,body=None):
    response=httpx.request(method,f'http://127.0.0.1:{port}{path}',headers=headers,json=body,timeout=30)
    response.raise_for_status(); return response.json()
def push(number, code, lock):
    files={'main.py':code,'requirements.lock':lock}; out=io.BytesIO()
    with zipfile.ZipFile(out,'w') as archive:
        for path,data in files.items(): archive.writestr(path,data)
    raw=out.getvalue(); digest=lambda data: hashlib.sha256(data).hexdigest()
    file=f'{uuid.uuid4()}.zip'; (stage/file).write_bytes(raw)
    return request('POST','/v1/dev-sessions/sync',{'session_id':session,'operation_id':str(uuid.uuid4()),'base_generation':number-1,'generation':number,'archive_sha256':digest(raw),'archive_size':len(raw),'staged_archive':file,'manifest':{'entrypoint':'main.py','python_requirement':'>=3.13,<3.14','lock_sha256':digest(lock),'files':[{'path':path,'size':len(data),'sha256':digest(data)} for path,data in files.items()],'deleted_paths':[]}})
def start(generation):
    body={'session_id':session,'robot_id':robot,'operation_id':str(uuid.uuid4()),'execution_id':str(uuid.uuid4()),'generation':generation}
    request('POST','/v1/dev-sessions/restart',body); return body
def wait(execution,wanted):
    for _ in range(200):
        state=request('GET',f'/v1/executions/{execution}')
        if state['state'] in wanted: return state
        time.sleep(.1)
    raise AssertionError('execution did not reach requested state')
def markers():
    code='import pathlib,sys; p=pathlib.Path("/data/dev-sessions")/sys.argv[1]/"envs"; print("\\n".join(sorted(x.name+":"+str(x.stat().st_mtime_ns) for x in p.glob("*.ready"))))'
    return subprocess.check_output(['docker','exec',name,'/app/.venv/bin/python','-c',code,session],text=True).strip().splitlines()
request('POST','/v1/dev-sessions/lifecycle',create)
push(1,b'print("one",flush=True)\n',b''); first=start(1)
assert wait(first['execution_id'],{'succeeded','failed'})['state']=='succeeded'
initial=markers(); assert len(initial)==1
push(2,b'print("two",flush=True)\n',b''); second=start(2)
assert wait(second['execution_id'],{'succeeded','failed'})['state']=='succeeded'
out=request('GET',f'/v1/executions/{second["execution_id"]}/output?stream=stdout&offset=0&limit=1024')
assert b'two' in base64.b64decode(out['data_base64']) and markers()==initial
push(3,b'print("three",flush=True)\n',b'# new lock\n'); third=start(3)
assert wait(third['execution_id'],{'succeeded','failed'})['state']=='succeeded' and len(markers())==2
push(4,b'import time\nprint("live",flush=True)\ntime.sleep(120)\n',b'# new lock\n'); live=start(4)
for _ in range(200):
    state=wait(live['execution_id'],{'running','failed'})
    output=request('GET',f'/v1/executions/{live["execution_id"]}/output?stream=stdout&offset=0&limit=1024')
    if b'live' in base64.b64decode(output['data_base64']): break
    time.sleep(.1)
else: raise AssertionError('live process did not start')
subprocess.run(['docker','restart',name],check=True,stdout=subprocess.DEVNULL)
port=int(subprocess.check_output(['docker','port',name,'8082/tcp'],text=True).strip().rsplit(':',1)[1])
for _ in range(60):
    try:
        if httpx.get(f'http://127.0.0.1:{port}/health/ready',timeout=1).status_code==200: break
    except httpx.HTTPError: pass
    time.sleep(.5)
else: raise AssertionError('Runner did not become ready')
replay=request('POST','/v1/dev-sessions/restart',live)
assert replay['execution']['state']=='failed' and replay['execution']['error']['code']=='interrupted_unknown_outcome'
request('POST','/v1/dev-sessions/lifecycle',{**create,'operation_id':str(uuid.uuid4()),'reason':'close'})
print('PASS: real Runner reused environment, reprovisioned changed lock, and did not replay interrupted execution')
PY
