"""Daemon-side client for the private Runner v1 diagnostic interface."""
import hashlib
import json
from pathlib import Path

import httpx

FIXTURE_DIGESTS = {
    "hello": "3d82b5528223fe6bc848adbc178daa49cc2ccc5b26129c3eea91e320c0a501e2",
    "long": "7e59dd8cf36f43064040201e11619b3f60b78209e9fb053a3df0463b28a04621",
}


class RunnerRejected(Exception):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code
        self.message = message


def reject_response(response):
    codes = {400: "invalid_input", 401: "runner_unauthenticated", 404: "not_found", 409: "execution_conflict", 413: "size_limit", 503: "capacity_exceeded"}
    code = codes.get(response.status_code, "runner_error")
    if response.status_code == 409:
        try:
            candidate = response.json()["error"]["code"]
            if candidate in {"conflict", "execution_conflict"}:
                code = candidate
        except (KeyError, TypeError, ValueError):
            pass
    raise RunnerRejected(code, "Runner rejected the operation.")


def staged_fixture(settings, name):
    if name not in FIXTURE_DIGESTS:
        raise RunnerRejected("invalid_input", "Unknown fixture.")
    data = (Path(__file__).parent / "fixtures" / name / "main.py").read_bytes()
    manifest = [["main.py", hashlib.sha256(data).hexdigest()]]
    digest = hashlib.sha256(json.dumps(manifest, ensure_ascii=False, separators=(",", ":")).encode()).hexdigest()
    if digest != FIXTURE_DIGESTS[name]:
        raise RunnerRejected("invalid_input", "Fixture digest mismatch.")
    directory = Path(settings.stage_dir) / "fixtures" / name
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / "main.py"
    if not path.exists() or path.read_bytes() != data:
        temporary = path.with_name(".main.py.tmp")
        temporary.write_bytes(data)
        temporary.replace(path)
    return {"revision_digest": digest, "code_ref": f"fixtures/{name}", "entrypoint": "main.py"}


async def execute(settings, operation):
    if not settings.runner_auth_token:
        raise RunnerRejected("runner_unauthenticated", "Runner credential unavailable.")
    headers = {"authorization": f"Bearer {settings.runner_auth_token}"}
    async with httpx.AsyncClient(base_url=settings.runner_url, headers=headers, timeout=5) as client:
        if operation.kind == "diagnostic.execution.start":
            body = {"execution_id": operation.operation_id, "robot_id": operation.robot_id, **staged_fixture(settings, operation.payload["fixture"])}
            response = await client.post("/v1/executions", json=body)
            if response.status_code not in (200, 201):
                reject_response(response)
            return {"execution": response.json()}
        execution_id = operation.payload["execution_id"]
        if operation.kind == "diagnostic.execution.stop":
            response = await client.post(f"/v1/executions/{execution_id}/stop", timeout=8)
            if response.status_code != 200:
                reject_response(response)
            return {"execution": response.json()}
        if operation.kind == "diagnostic.execution.inspect":
            response = await client.get(f"/v1/executions/{execution_id}")
            if response.status_code != 200:
                reject_response(response)
            execution = response.json()
            response = await client.get(f"/v1/executions/{execution_id}/output", params={key: operation.payload[key] for key in ("stream", "offset", "limit")})
            if response.status_code != 200:
                reject_response(response)
            return {"execution": execution, "output": response.json()}
        raise RunnerRejected("invalid_input", "Unsupported Runner operation.")
