#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
set -a
. ./.auth.env
set +a
export RINGFOUR_AUTH0_DOMAIN="$AUTH0_DOMAIN" RINGFOUR_AUTH0_CLIENT_ID="$AUTH0_CLI_CLIENT_ID" RINGFOUR_AUTH0_AUDIENCE="$AUTH0_AUDIENCE"
robot=${RINGFOUR_ROBOT_ID:?Set RINGFOUR_ROBOT_ID to the enrolled target robot ID}
cli=(uv run --project apps/cli ringfour-cli)
json_id() { python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])'; }
wait_op() {
  local id=$1 body state
  for _ in $(seq 1 45); do
    body=$("${cli[@]}" robot operation "$robot" "$id") || return 1
    state=$(python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])' <<<"$body") || return 1
    case "$state" in succeeded) printf '%s\n' "$body"; return 0;; failed|cancelled) printf '%s\n' "$body" >&2; return 1;; esac
    sleep 1
  done
  echo "Timed out waiting for operation $id" >&2; return 1
}
inspect() {
  local execution=$1 inspect_id
  inspect_id=$("${cli[@]}" robot fixture-inspect "$robot" "$execution" | json_id) || return 1
  wait_op "$inspect_id"
}
wait_execution() {
  local id=$1 expected=$2 body state
  for _ in $(seq 1 45); do
    body=$(inspect "$id") || return 1
    state=$(python3 -c 'import json,sys; print(json.load(sys.stdin)["result"]["execution"]["state"])' <<<"$body") || return 1
    if [ "$state" = "$expected" ]; then printf '%s\n' "$body"; return 0; fi
    if [ "$state" = failed ] && [ "$expected" != failed ]; then printf '%s\n' "$body" >&2; return 1; fi
    sleep 1
  done
  echo "Timed out waiting for execution $id to reach $expected" >&2; return 1
}
case "${1:-}" in
  hello)
    id=$("${cli[@]}" robot fixture-start "$robot" hello | json_id)
    wait_op "$id" >/dev/null
    body=$(wait_execution "$id" succeeded)
    python3 -c 'import json,sys,base64; x=json.load(sys.stdin)["result"]; y=x["execution"]; output=base64.b64decode(x["output"]["data_base64"]); assert y["exit_code"] == 0 and b"Hello from Ringfour Runner" in output; print("HELLO execution", y["execution_id"], "state",y["state"],"exit",y["exit_code"],"output",repr(output.decode()))' <<<"$body"
    ;;
  start-long)
    id=$("${cli[@]}" robot fixture-start "$robot" long | json_id)
    wait_op "$id" >/dev/null
    wait_execution "$id" running >/dev/null
    echo "LONG execution $id running"
    ;;
  verify-hello)
    body=$(wait_execution "$2" succeeded)
    python3 -c 'import json,sys,base64; x=json.load(sys.stdin)["result"]; y=x["execution"]; output=base64.b64decode(x["output"]["data_base64"]); assert y["exit_code"] == 0 and b"Hello from Ringfour Runner" in output; print("PERSISTED execution", y["execution_id"], "state",y["state"],"exit",y["exit_code"],"output",repr(output.decode()))' <<<"$body"
    ;;
  overlap)
    id=$("${cli[@]}" robot fixture-start "$robot" hello | json_id)
    for _ in $(seq 1 45); do
      body=$("${cli[@]}" robot operation "$robot" "$id")
      state=$(python3 -c 'import json,sys; print(json.load(sys.stdin)["state"])' <<<"$body")
      if [ "$state" = failed ]; then
        python3 -c 'import json,sys; x=json.load(sys.stdin); assert x["result"]["error"]["code"]=="execution_conflict"; print("OVERLAP operation",x["id"],"state",x["state"],"error",x["result"]["error"]["code"])' <<<"$body"
        exit 0
      fi
      [ "$state" != succeeded ] || { echo 'overlapping execution unexpectedly succeeded' >&2; exit 1; }
      sleep 1
    done
    echo 'overlap timed out' >&2; exit 1
    ;;
  inspect-running) wait_execution "$2" running >/dev/null; echo "LONG execution $2 still running";;
  stop)
    id=$("${cli[@]}" robot fixture-stop "$robot" "$2" | json_id)
    wait_op "$id" >/dev/null
    body=$(wait_execution "$2" cancelled)
    python3 -c 'import json,sys; x=json.load(sys.stdin)["result"]["execution"]; print("STOP execution",x["execution_id"],"state",x["state"],"exit",x["exit_code"])' <<<"$body"
    ;;
  inspect-failed)
    body=$(wait_execution "$2" failed)
    python3 -c 'import json,sys; x=json.load(sys.stdin)["result"]["execution"]; assert x["error"]["code"]=="interrupted_unknown_outcome"; print("RESTART execution",x["execution_id"],"state",x["state"],"error",x["error"]["code"])' <<<"$body"
    ;;
  *) echo 'usage: hello|start-long|inspect-running ID|stop ID|inspect-failed ID|verify-hello ID|overlap' >&2; exit 2;;
esac
