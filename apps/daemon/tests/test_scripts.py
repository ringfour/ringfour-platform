import asyncio
import base64
import hashlib
import io
import json
import zipfile
from datetime import UTC, datetime, timedelta

import httpx
import pytest

from ringfour_daemon import operations, runner, scripts
from ringfour_daemon.config import Settings
from ringfour_daemon.db import migrate


def sha(value):
    return hashlib.sha256(value).hexdigest()


def sample():
    files = {"main.py": b"print('hello')\n", "requirements.lock": b""}
    memory = io.BytesIO()
    with zipfile.ZipFile(memory, "w") as archive:
        for name, value in files.items():
            archive.writestr(name, value)
    contents = memory.getvalue()
    manifest = [[name, sha(data)] for name, data in sorted(files.items())]
    payload = {"archive_size": len(contents), "archive_sha256": sha(contents),
               "revision_digest": sha(json.dumps(manifest, separators=(",", ":")).encode()),
               "entrypoint": "main.py", "lock_sha256": sha(b"")}
    return contents, payload


def test_staging_verifies_digest_and_reuses_immutable_bundle(tmp_path):
    archive, payload = sample()
    reference = scripts.unpack_verified(archive, payload, tmp_path)
    assert (tmp_path / reference / "main.py").read_bytes() == b"print('hello')\n"
    assert scripts.unpack_verified(archive, payload, tmp_path) == reference
    (tmp_path / reference / "main.py").write_bytes(b"tampered")
    with pytest.raises(runner.RunnerRejected):
        scripts.unpack_verified(archive, payload, tmp_path)
    with pytest.raises(runner.RunnerRejected):
        scripts.unpack_verified(archive, {**payload, "revision_digest": "../escape"}, tmp_path)
    with pytest.raises(runner.RunnerRejected):
        scripts.unpack_verified(archive + b"x", payload, tmp_path)


def test_output_retry_resumes_without_duplicate_bytes(tmp_path, monkeypatch):
    db = str(tmp_path / "daemon.db")
    migrate(db)
    execution = "4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b"
    operations.record_command(db, "robot", {"operation_id": execution, "kind": "script.execution.start",
        "payload_version": 1, "payload": {}, "deadline_at": (datetime.now(UTC) + timedelta(minutes=1)).isoformat()})
    scripts.record_sync(db, execution)
    credential = tmp_path / "credential"
    credential.write_text("secret")
    settings = Settings(db_path=db, runner_url="http://runner", credential_path=str(credential), runner_auth_token="runner-secret")
    identity = {"credential_id": "credential-id", "platform_url": "http://platform"}
    attempts = []
    uploaded = []
    artifact = b"result"
    real_client = httpx.AsyncClient

    def handler(request):
        path = request.url.path
        if request.url.host == "runner":
            if path.endswith("/output"):
                stream, offset = request.url.params["stream"], int(request.url.params["offset"])
                data = b"hello"[offset:] if stream == "stdout" else b""
                return httpx.Response(200, json={"offset": offset, "next_offset": offset + len(data),
                    "data_base64": base64.b64encode(data).decode(), "truncated": False})
            if path.endswith("/artifacts"):
                return httpx.Response(200, json={"artifacts": [{"path": "result.txt", "size": len(artifact), "sha256": sha(artifact)}]})
            if path.endswith("/artifacts/content"):
                return httpx.Response(200, json={"offset": 0, "next_offset": len(artifact), "data_base64": base64.b64encode(artifact).decode()})
            return httpx.Response(200, json={"state": "succeeded", "exit_code": 0, "error": None})
        if "/output/" in path:
            attempts.append((request.url.params["offset"], request.content))
            if len(attempts) == 1:
                return httpx.Response(503)
        if path.endswith("/artifacts"):
            uploaded.append(request.content)
        return httpx.Response(204)

    monkeypatch.setattr(scripts.httpx, "AsyncClient", lambda **kwargs: real_client(transport=httpx.MockTransport(handler), **kwargs))
    row = scripts.sync_rows(db)[0]
    with pytest.raises(httpx.HTTPStatusError):
        asyncio.run(scripts.synchronize(settings, identity, row))
    assert scripts.sync_rows(db)[0][1] == 0
    asyncio.run(scripts.synchronize(settings, identity, scripts.sync_rows(db)[0]))
    assert attempts == [("0", b"hello"), ("0", b"hello")]
    assert uploaded == [artifact]
    assert scripts.sync_rows(db) == []


def test_missing_confirmed_runner_execution_reports_unknown_outcome(tmp_path, monkeypatch):
    db = str(tmp_path / "daemon.db")
    migrate(db)
    execution = "4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b"
    operations.record_command(db, "robot", {"operation_id": execution, "kind": "script.execution.start",
        "payload_version": 1, "payload": {}, "deadline_at": (datetime.now(UTC) + timedelta(minutes=1)).isoformat()})
    scripts.record_sync(db, execution)
    scripts.mark_started(db, execution)
    credential = tmp_path / "credential"
    credential.write_text("secret")
    settings = Settings(db_path=db, runner_url="http://runner", credential_path=str(credential), runner_auth_token="runner-secret")
    identity = {"credential_id": "credential-id", "platform_url": "http://platform"}
    statuses = []
    real_client = httpx.AsyncClient

    def handler(request):
        if request.url.host == "runner":
            return httpx.Response(404)
        statuses.append(json.loads(request.content))
        return httpx.Response(204)

    monkeypatch.setattr(scripts.httpx, "AsyncClient", lambda **kwargs: real_client(transport=httpx.MockTransport(handler), **kwargs))
    asyncio.run(scripts.synchronize(settings, identity, scripts.sync_rows(db)[0]))
    assert statuses[0]["state"] == "failed"
    assert statuses[0]["error"]["code"] == "interrupted_unknown_outcome"
    assert scripts.sync_rows(db) == []


def test_start_payload_accepts_only_a_valid_optional_mission_revision():
    _, bundle = sample()
    payload = {**bundle, "revision_id": "7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e", "python_requirement": ">=3.13,<3.14"}
    scripts.validate_start_payload(payload)
    scripts.validate_start_payload({**payload, "mission_revision_id": "9b8a7c6d-5e4f-4a3b-8c7d-6e5f4a3b2c1d"})
    with pytest.raises(runner.RunnerRejected):
        scripts.validate_start_payload({**payload, "mission_revision_id": "not-a-uuid"})
    with pytest.raises(runner.RunnerRejected):
        scripts.validate_start_payload({**payload, "unexpected": True})
