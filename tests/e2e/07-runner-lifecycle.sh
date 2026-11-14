#!/usr/bin/env bash
# Actual daemon client over HTTP to an isolated Runner container.
set -euo pipefail
cd "$(dirname "$0")/../.."
name="rf07-runner-$$"
cleanup() { docker rm -f "$name" >/dev/null 2>&1 || true; }
trap cleanup EXIT
docker build -q -t ringfour-runner:07-test apps/runner >/dev/null
docker run -d --name "$name" -p 127.0.0.1::8082 -e RUNNER_AUTH_TOKEN=local-test-secret ringfour-runner:07-test >/dev/null
port=$(docker port "$name" 8082/tcp | sed 's/.*://')
for _ in $(seq 1 60); do curl -sf "http://127.0.0.1:$port/health/ready" >/dev/null && break; sleep 1; done
uv run --project apps/daemon python - "$port" <<'PY'
import asyncio, sys, uuid
from datetime import UTC, datetime
from ringfour_daemon import operations, runner
from ringfour_daemon.config import Settings
settings = Settings(db_path='/tmp/unused', runner_url=f'http://127.0.0.1:{sys.argv[1]}', runner_auth_token='local-test-secret')
session = str(uuid.uuid4())
def make(kind, reason):
    oid = str(uuid.uuid4())
    return operations.DeviceOperation(operation_id=oid, robot_id=str(uuid.uuid4()), kind=kind, payload_version=1,
        payload={'operation_id':oid,'session_id':session,'expires_at':'2027-01-01T00:00:00Z','reason':reason},
        state='running', deadline_at=datetime.now(UTC).isoformat(), updated_at=datetime.now(UTC).isoformat(), result=None)
create = make('dev.session.create', 'create')
first = asyncio.run(runner.dev_session_lifecycle(settings, create))
assert first['state'] == 'ready' and first == asyncio.run(runner.dev_session_lifecycle(settings, create))
close = make('dev.session.close', 'close')
assert asyncio.run(runner.dev_session_lifecycle(settings, close))['state'] == 'closed'
print('PASS: actual daemon client created, replayed and closed a Runner workspace over HTTP')
PY
