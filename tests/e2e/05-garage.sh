#!/usr/bin/env bash
# Disposable single-node Garage compatibility test. Never uses the platform DB.
set -euo pipefail
cd "$(dirname "$0")/../.."
test_dir=$(mktemp -d "${TMPDIR:-/tmp}/ringfour-garage-05.XXXXXXXX")
container="ringfour-garage-05-$(openssl rand -hex 6)"
cleanup() {
  docker rm -f "$container" >/dev/null 2>&1 || true
  rm -rf "$test_dir"
}
trap cleanup EXIT
export GARAGE_DEFAULT_ACCESS_KEY="GK$(openssl rand -hex 16)"
export GARAGE_DEFAULT_SECRET_KEY="$(openssl rand -hex 32)"
export GARAGE_DEFAULT_BUCKET="ringfour-test"
export AWS_ACCESS_KEY_ID="$GARAGE_DEFAULT_ACCESS_KEY"
export AWS_SECRET_ACCESS_KEY="$GARAGE_DEFAULT_SECRET_KEY"
export AWS_DEFAULT_REGION=garage
rpc_secret=$(openssl rand -hex 32)
cat > "$test_dir/garage.toml" <<EOF
metadata_dir = "/tmp/meta"
data_dir = "/tmp/data"
db_engine = "sqlite"
replication_factor = 1
rpc_bind_addr = "[::]:3901"
rpc_public_addr = "127.0.0.1:3901"
rpc_secret = "$rpc_secret"
[s3_api]
s3_region = "garage"
api_bind_addr = "[::]:3900"
root_domain = ".s3.garage.localhost"
EOF
docker run -d --name "$container" -p 127.0.0.1::3900 \
  -v "$test_dir/garage.toml:/etc/garage.toml:ro" \
  -e GARAGE_DEFAULT_ACCESS_KEY -e GARAGE_DEFAULT_SECRET_KEY -e GARAGE_DEFAULT_BUCKET \
  dxflrs/garage:v2.5.0 /garage server --single-node --default-bucket >/dev/null
port=$(docker port "$container" 3900/tcp)
export GARAGE_TEST_ENDPOINT="http://$port"
uv run --no-project --with 'boto3==1.40.61' --with 'botocore==1.40.76' python - "$GARAGE_TEST_ENDPOINT" <<'PY'
import hashlib
import sys
import time
import urllib.request
import boto3
from botocore.config import Config
from botocore.exceptions import BotoCoreError, ClientError

endpoint = sys.argv[1]
client = boto3.client("s3", endpoint_url=endpoint, config=Config(
    signature_version="s3v4", s3={"addressing_style": "path"},
    retries={"max_attempts": 0}, connect_timeout=1, read_timeout=2))
for attempt in range(60):
    try:
        client.head_bucket(Bucket="ringfour-test")
        break
    except (BotoCoreError, ClientError):
        if attempt == 59:
            raise
        time.sleep(0.5)

bucket = "ringfour-test"
original = b"verified script revision\n"
client.put_object(Bucket=bucket, Key="incoming/test", Body=original)
data = client.get_object(Bucket=bucket, Key="incoming/test")["Body"].read()
assert hashlib.sha256(data).digest() == hashlib.sha256(original).digest()
client.put_object(Bucket=bucket, Key="final/test", Body=data)
client.put_object(Bucket=bucket, Key="incoming/test", Body=b"later overwrite")
assert client.get_object(Bucket=bucket, Key="final/test")["Body"].read() == original
url = client.generate_presigned_url("get_object", Params={"Bucket": bucket, "Key": "final/test"}, ExpiresIn=60)
with urllib.request.urlopen(url, timeout=5) as response:
    assert response.read() == original
client.delete_object(Bucket=bucket, Key="incoming/test")
client.delete_object(Bucket=bucket, Key="final/test")
print("PASS Garage 2.5.0: authenticated put/get, verified promotion, presigned GET and deletion")
PY
docker run --rm \
  -e AWS_ACCESS_KEY_ID -e AWS_SECRET_ACCESS_KEY \
  -e "GARAGE_TEST_ENDPOINT=http://host.docker.internal:${port##*:}" \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  -v "$PWD":/repo -v ringfour-dotnet-nuget:/root/.nuget/packages \
  -v ringfour-api-obj:/repo/apps/api/src/Ringfour.Api/obj \
  -v ringfour-api-bin:/repo/apps/api/src/Ringfour.Api/bin \
  --tmpfs /repo/tests/integration/garage-probe/obj \
  --tmpfs /repo/tests/integration/garage-probe/bin \
  -w /repo/tests/integration/garage-probe \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet run
