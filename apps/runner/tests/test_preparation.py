import hashlib
import os
import time
import uuid
from pathlib import Path

from fastapi.testclient import TestClient

from ringfour_runner.config import Settings
from ringfour_runner.executions import bundle_files
from ringfour_runner.main import create_app
from ringfour_runner.preparation import collect_cache, prepared_environment

HEADERS = {"authorization": "Bearer test-secret"}
REQUIREMENT = ">=3.13,<3.14"


def candidate(settings, name, code, lock=b""):
    folder = Path(settings.stage_dir) / name
    folder.mkdir(parents=True)
    (folder / "main.py").write_text(code)
    (folder / "requirements.lock").write_bytes(lock)
    _, digest = bundle_files(settings, name, "main.py")
    return {"revision_digest": digest, "code_ref": name, "entrypoint": "main.py",
            "python_requirement": REQUIREMENT, "lock_sha256": hashlib.sha256(lock).hexdigest(), "retain_digests": []}


def test_prepare_is_idempotent_does_not_execute_code_and_failure_preserves_previous(tmp_path):
    settings = Settings(str(tmp_path / "data"), str(tmp_path / "stage"), "test-secret")
    side_effect = tmp_path / "ran"
    first = candidate(settings, "first", f"from pathlib import Path\nPath({str(side_effect)!r}).write_text('bad')\n")
    second = candidate(settings, "second", "print('new')\n", b"not a valid @@@ lock\n")
    with TestClient(create_app(settings)) as client:
        assert client.post("/v1/revisions/prepare", json=first).status_code == 401
        result = client.post("/v1/revisions/prepare", json=first, headers=HEADERS)
        assert result.status_code == 200 and result.json()["state"] == "ready"
        original = prepared_environment(settings, first["revision_digest"], REQUIREMENT, first["lock_sha256"])
        assert original is not None and not side_effect.exists()
        assert client.post("/v1/revisions/prepare", json=first, headers=HEADERS).status_code == 200
        assert prepared_environment(settings, first["revision_digest"], REQUIREMENT, first["lock_sha256"]) == original
        failed = client.post("/v1/revisions/prepare", json={**second, "retain_digests": [first["revision_digest"]]}, headers=HEADERS)
        assert failed.status_code == 503 and failed.json()["error"]["code"] == "preparation_failed"
        assert prepared_environment(settings, first["revision_digest"], REQUIREMENT, first["lock_sha256"]) == original
        assert prepared_environment(settings, second["revision_digest"], REQUIREMENT, second["lock_sha256"]) is None


def test_prepare_rejects_active_execution_and_bad_lock(tmp_path):
    settings = Settings(str(tmp_path / "data"), str(tmp_path / "stage"), "test-secret")
    body = candidate(settings, "active", "import time\ntime.sleep(60)\n")
    execution = {key: body[key] for key in ("revision_digest", "code_ref", "entrypoint", "python_requirement", "lock_sha256")}
    execution.update(execution_id=str(uuid.uuid4()), robot_id=str(uuid.uuid4()))
    with TestClient(create_app(settings)) as client:
        assert client.post("/v1/revisions/prepare", json={**body, "lock_sha256": "0" * 64}, headers=HEADERS).status_code == 400
        assert client.post("/v1/executions", json=execution, headers=HEADERS).status_code == 201
        for _ in range(100):
            if client.get(f"/v1/executions/{execution['execution_id']}", headers=HEADERS).json()["state"] == "running":
                break
            time.sleep(0.05)
        assert client.post("/v1/revisions/prepare", json=body, headers=HEADERS).status_code == 409
        assert client.post(f"/v1/executions/{execution['execution_id']}/stop", headers=HEADERS).status_code == 200


def test_start_uses_prepared_environment(tmp_path, monkeypatch):
    from ringfour_runner import executions

    settings = Settings(str(tmp_path / "data"), str(tmp_path / "stage"), "test-secret")
    body = candidate(settings, "mission", "print('prepared run')\n")
    execution = {key: body[key] for key in ("revision_digest", "code_ref", "entrypoint", "python_requirement", "lock_sha256")}
    execution.update(execution_id=str(uuid.uuid4()), robot_id=str(uuid.uuid4()))

    async def unexpected(*args):
        raise AssertionError("A ready environment must not be prepared again")

    with TestClient(create_app(settings)) as client:
        assert client.post("/v1/revisions/prepare", json=body, headers=HEADERS).status_code == 200
        monkeypatch.setattr(executions, "prepare_execution_environment", unexpected)
        assert client.post("/v1/executions", json=execution, headers=HEADERS).status_code == 201
        for _ in range(100):
            result = client.get(f"/v1/executions/{execution['execution_id']}", headers=HEADERS).json()
            if result["state"] in {"succeeded", "failed", "cancelled"}:
                break
            time.sleep(0.05)
        assert result["state"] == "succeeded" and result["exit_code"] == 0


def test_cache_collection_preserves_selected_digest(tmp_path):
    settings = Settings(str(tmp_path / "data"), str(tmp_path / "stage"), "test-secret")
    root = Path(settings.data_dir) / "prepared-envs"
    markers = Path(settings.data_dir) / "prepared"
    root.mkdir(parents=True)
    markers.mkdir()
    digests = [f"{index:064x}" for index in range(21)]
    for index, digest in enumerate(digests):
        folder = root / digest
        folder.mkdir()
        (markers / f"{digest}.json").write_text("{}")
        os.utime(folder, (index + 1, index + 1))
    collect_cache(settings, {digests[0], digests[-1]})
    assert (root / digests[0]).exists() and (root / digests[-1]).exists()
    assert not (root / digests[1]).exists()
    assert not (markers / f"{digests[1]}.json").exists()
    assert len(list(root.iterdir())) == 20
