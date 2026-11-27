import asyncio
import copy
import json
import uuid
from pathlib import Path

import httpx
import pytest
from jsonschema.exceptions import ValidationError
from ringfour_daemon import agent_inventory, app_validation, operations
from ringfour_daemon import app_installations as apps
from ringfour_daemon.app_engine import EngineError, Executor
from ringfour_daemon.app_protocol import Platform
from ringfour_daemon.config import Settings
from ringfour_daemon.db import connect, migrate

ROOT = Path(__file__).resolve().parents[3] / "packages/contracts"


def uid():
    return str(uuid.uuid4())


@pytest.fixture
def device(tmp_path, monkeypatch, request):
    db = str(tmp_path / "daemon.db")
    migrate(db)
    robot, namespace, app, revision, configuration, installation = [
        uid() for _ in range(6)
    ]
    with connect(db) as c:
        c.execute(
            "INSERT INTO device_identity VALUES (1,?,?,?,?)",
            (robot, uid(), "http://api", uid()),
        )
        c.execute(
            "INSERT INTO device_deployment_namespace VALUES (1,?,?)",
            (namespace, "2026-11-24T00:00:00Z"),
        )
    settings = Settings(
        db_path=db,

        apps_enabled=True,
        agent_uri="ws://agent",
    )
    manifest = json.loads((ROOT / f"fixtures/apps/v{getattr(request, 'param', 1)}/manifest.json").read_text())
    target = manifest["targets"][0]
    calls = []

    class Engine:
        failure = None

        def close(self):
            pass

        def platform(self):
            return Platform(os="linux", architecture="amd64", native_facts=[])

        def pull(self, target, auth):
            calls.append("pull")
            if self.failure:
                raise self.failure
            return self.image(target["image"])

        def image(self, image):
            return {
                "Id": "sha256:" + "b" * 64,
                "RepoDigests": [image],
                "Os": "linux",
                "Architecture": "amd64",
                "Config": {"Entrypoint": ["python", "/app/main.py"], "User": "10001"},
            }

        def request(self, *args, **kwargs):
            raise AssertionError("Installation invoked a container operation")

    engine = Engine()
    executor = Executor(settings, engine).open()

    async def fresh(settings, manifest):
        calls.append("fresh")
        return {"status": "compatible", "target": target}

    monkeypatch.setattr(agent_inventory, "check_fresh", fresh)
    reports = []
    broken = {"report": False, "manifest": False}

    def handler(request):
        if request.url.path.startswith("/v1/device/app-revisions/"):
            return httpx.Response(
                200,
                json={
                    "id": revision,
                    "app_id": app,
                    "manifest": (
                        {**manifest, "release": "tampered"}
                        if broken["manifest"]
                        else manifest
                    ),
                },
            )
        if request.url.path == "/v1/device/app-registry-grants":
            calls.append("grant")
            body = json.loads(request.content)
            return httpx.Response(
                200,
                json={
                    **body,
                    "robot_id": robot,
                    "registry": target["image"].split("/", 1)[0],
                    "repository": target["image"].split("/", 1)[1].split("@", 1)[0],
                    "expires_at": "2099-01-01T00:00:00Z",
                    "authorization": {"kind": "bearer", "token": "ephemeral-secret"},
                },
            )
        if request.url.path == "/v1/device/app-observations":
            if broken["report"]:
                raise httpx.ConnectError("offline")
            report = json.loads(request.content)
            reports.append(report)
            return httpx.Response(
                200,
                json={
                    "report_id": report["report_id"],
                    "report_sequence": report["report_sequence"],
                    "received_at": "2026-11-24T00:00:00Z",
                },
            )
        raise AssertionError(request.url)

    original = httpx.AsyncClient
    monkeypatch.setattr(
        httpx,
        "AsyncClient",
        lambda **kwargs: original(**kwargs, transport=httpx.MockTransport(handler)),
    )
    payload = {
        "app_protocol_version": 1,
        "deployment_namespace": namespace,
        "installation_id": installation,
        "expected_revision_id": None,
        "expected_configuration_revision_id": None,
        "release": {
            "app_id": app,
            "revision_id": revision,
            "manifest_digest": app_validation.digest(manifest),
            "configuration_revision_id": configuration,
            "configuration": manifest["configuration"]["defaults"],
            "target": target,
        },
    }

    def command(payload=payload, kind="install", deadline="2099-01-01T00:00:00Z"):
        return operations.record_command(
            db,
            robot,
            {
                "operation_id": uid(),
                "kind": "app." + kind,
                "payload_version": 1,
                "payload": copy.deepcopy(payload),
                "deadline_at": deadline,
            },
        )[0]

    yield settings, executor, command, payload, calls, reports, broken
    executor.close()


def process(settings, executor, operation):
    asyncio.run(apps.process(settings, executor, operation, {}))


@pytest.mark.parametrize("device", [1, 2], indirect=True)
def test_install_checks_fresh_before_pull_and_creates_zero_runs_or_containers(device):
    settings, executor, command, payload, calls, reports, _ = device
    operation = command()
    process(settings, executor, operation)
    assert calls == ["fresh", "grant", "pull"]
    assert len(reports) == 1
    report = reports[0]
    assert report["runs"] == [] and report["operations"][0]["state"] == "succeeded"
    with connect(settings.db_path) as conn:
        assert conn.execute("SELECT count(*) FROM app_runs").fetchone() == (0,)
        assert conn.execute(
            "SELECT revision_id,candidate_json FROM app_installations"
        ).fetchone() == (payload["release"]["revision_id"], None)
        assert "ephemeral-secret" not in "\n".join(conn.iterdump())
    process(settings, executor, operation)
    assert calls == ["fresh", "grant", "pull"] and len(reports) == 1


def test_crash_after_pull_and_lost_installed_receipt_replay_same_operation(device):
    settings, executor, command, payload, calls, reports, broken = device
    operation = command()
    original = executor.engine.pull

    def crash(target, auth):
        original(target, auth)
        raise SystemExit("simulated process crash")

    executor.engine.pull = crash
    with pytest.raises(SystemExit):
        process(settings, executor, operation)
    with connect(settings.db_path) as conn:
        assert conn.execute("SELECT revision_id FROM app_installations").fetchone() == (
            None,
        )
    executor.engine.pull = original
    broken["report"] = True
    with pytest.raises(httpx.ConnectError):
        process(settings, executor, operation)
    with connect(settings.db_path) as conn:
        saved = json.loads(
            conn.execute(
                "SELECT terminal_report_json FROM app_preparations"
            ).fetchone()[0]
        )
        assert conn.execute("SELECT revision_id FROM app_installations").fetchone() == (
            payload["release"]["revision_id"],
        )
    before = len(calls)
    broken["report"] = False
    process(settings, executor, operation)
    assert len(calls) == before and reports[0] == saved
    assert reports[0]["operations"][0]["operation_id"] == operation.operation_id


def test_failed_update_keeps_installed_binding_and_configure_skips_pull(device):
    settings, executor, command, payload, calls, reports, _ = device
    process(settings, executor, command())
    updated = copy.deepcopy(payload)
    updated["expected_revision_id"] = payload["release"]["revision_id"]
    updated["expected_configuration_revision_id"] = payload["release"][
        "configuration_revision_id"
    ]
    updated["release"]["configuration_revision_id"] = uid()
    updated["release"]["configuration"] = {"sample_count": 2}
    for failure_code in ("pull_failed", "storage_unavailable", "engine_unavailable"):
        executor.engine.failure = EngineError(failure_code)
        operation = command(updated, "update")
        process(settings, executor, operation)
        assert reports[-1]["operations"][0]["failure"]["code"] == failure_code
        assert (
            reports[-1]["installations"][0]["installed"]["configuration_revision_id"]
            == payload["release"]["configuration_revision_id"]
        )
        assert reports[-1]["installations"][0]["candidate"]["state"] == "failed"
    executor.engine.failure = None
    before = calls.count("pull")
    process(settings, executor, command(updated, "configure"))
    assert (
        calls.count("pull") == before
        and reports[-1]["operations"][0]["state"] == "succeeded"
    )
    assert (
        reports[-1]["installations"][0]["installed"]["configuration_revision_id"]
        == updated["release"]["configuration_revision_id"]
    )


def test_inventory_failure_and_bad_manifest_never_pull(device, monkeypatch):
    settings, executor, command, _payload, calls, reports, broken = device

    async def unavailable(*args):
        return {"status": "inventory_unavailable", "inventory_observed_at": None}

    monkeypatch.setattr(agent_inventory, "check_fresh", unavailable)
    process(settings, executor, command())
    assert "pull" not in calls
    assert reports[-1]["operations"][0]["failure"]["code"] == "inventory_unavailable"
    broken["manifest"] = True
    process(settings, executor, command())
    assert "pull" not in calls
    assert reports[-1]["operations"][0]["failure"]["code"] == "revision_unavailable"


def test_local_busy_expired_or_stale_generation_preserves_installed_binding(device):
    settings, executor, command, payload, calls, reports, _ = device
    operation = command(deadline="2000-01-01T00:00:00Z")
    process(settings, executor, operation)
    assert (
        reports[-1]["operations"][0]["failure"]["code"] == "deadline_exceeded"
        and not calls
    )
    process(settings, executor, command())
    assert reports[-1]["operations"][0]["state"] == "succeeded"
    with pytest.raises(apps.PreparationError, match="stale_installation"):
        process(settings, executor, command())
    changed = copy.deepcopy(payload)
    changed["expected_revision_id"] = payload["release"]["revision_id"]
    changed["expected_configuration_revision_id"] = payload["release"][
        "configuration_revision_id"
    ]
    changed["release"]["configuration_revision_id"] = uid()
    with connect(settings.db_path) as conn:
        conn.execute(
            "INSERT INTO device_execution_slots VALUES (1,?,'legacy',?,?)",
            (executor.identity["robot_id"], uid(), "2026-11-24T00:00:00Z"),
        )
    process(settings, executor, command(changed, "update"))
    assert reports[-1]["operations"][0]["failure"]["code"] == "execution_conflict"
    assert (
        reports[-1]["installations"][0]["installed"]["configuration_revision_id"]
        == payload["release"]["configuration_revision_id"]
    )


def test_vendored_contracts_and_default_configuration_are_independent():
    paths = {
        "manifest": "apps/v1/manifest.schema.json",
        "api": "apps/v1/api.schema.json",
        "device": "device/apps/v1/protocol.schema.json",
        "error": "errors/v2/app-error.schema.json",
    }
    for name, path in paths.items():
        assert app_validation.SCHEMAS[name] == json.loads(
            (ROOT / "schemas" / path).read_text()
        )
    manifest = json.loads((ROOT / "fixtures/apps/v1/manifest.json").read_text())
    app_validation.validate_configuration(
        manifest, manifest["configuration"]["defaults"]
    )
    for value in (
        {},
        {"sample_count": True},
        {"sample_count": 21},
        {"sample_count": 1, "extra": 2},
    ):
        with pytest.raises(ValidationError):
            app_validation.validate_configuration(manifest, value)


def test_canonical_unicode_fixture_matches_api_and_cli():
    fixture = json.loads((ROOT / "fixtures/apps/v1/canonical-unicode.json").read_text())
    assert (
        app_validation.digest(app_validation.validate(fixture["manifest"], "manifest"))
        == fixture["digest"]
    )


def test_shutdown_releases_writer_only_after_pull_and_refuses_late_application(device):
    import threading

    settings, executor, command, _payload, _calls, _reports, _broken = device
    operation = command()
    apps.begin(settings, executor, operation)
    started = threading.Event()
    release = threading.Event()
    closed = threading.Event()

    def pull(*args):
        started.set()
        release.wait(timeout=5)
        return {}

    executor.engine.pull = pull
    worker = threading.Thread(target=executor.prepare_image, args=({}, {}))
    worker.start()
    assert started.wait(timeout=2)

    def close():
        executor.close()
        closed.set()

    closer = threading.Thread(target=close)
    closer.start()
    assert not closed.wait(timeout=0.05)
    release.set()
    worker.join(timeout=2)
    closer.join(timeout=2)
    assert closed.is_set()
    with pytest.raises(EngineError, match="ownership_conflict"):
        apps.finish(settings, executor, operation)
    with connect(settings.db_path) as conn:
        assert conn.execute("SELECT revision_id FROM app_installations").fetchone() == (
            None,
        )


def test_uninstall_replays_durable_report_and_preserves_new_installation(device):
    settings, executor, command, payload, calls, reports, broken = device
    process(settings, executor, command())
    removal = {key: value for key, value in payload.items() if key != "release"}
    removal.update(expected_revision_id=payload["release"]["revision_id"],
                   expected_configuration_revision_id=payload["release"]["configuration_revision_id"])
    operation = command(removal, "uninstall")
    assert apps.begin_uninstall(settings, executor, operation) == "staging"
    # Resume after interruption before removal; then lose the API receipt.
    broken["report"] = True
    with pytest.raises(httpx.ConnectError):
        asyncio.run(apps.process_uninstall(settings, executor, operation, {}))
    with connect(settings.db_path) as conn:
        assert conn.execute("SELECT revision_id FROM app_installations").fetchone() == (None,)
    broken["report"] = False
    asyncio.run(apps.process_uninstall(settings, executor, operation, {}))
    assert reports[-1]["operations"][0]["kind"] == "uninstall"
    assert reports[-1]["operations"][0]["state"] == "succeeded"
    replacement = copy.deepcopy(payload)
    replacement["installation_id"] = uid()
    process(settings, executor, command(replacement))
    asyncio.run(apps.process_uninstall(settings, executor, operation, {}))
    with connect(settings.db_path) as conn:
        assert conn.execute("SELECT revision_id FROM app_installations WHERE installation_id=?",
                            (replacement["installation_id"],)).fetchone() == (payload["release"]["revision_id"],)
    assert calls.count("pull") == 2


def test_stale_uninstall_fails_without_removing_binding(device):
    settings, executor, command, payload, _, reports, _ = device
    process(settings, executor, command())
    removal = {key: value for key, value in payload.items() if key != "release"}
    removal.update(expected_revision_id=uid(),
                   expected_configuration_revision_id=payload["release"]["configuration_revision_id"])
    asyncio.run(apps.process_uninstall(settings, executor, command(removal, "uninstall"), {}))
    assert reports[-1]["operations"][0]["failure"]["code"] == "stale_installation"
    with connect(settings.db_path) as conn:
        assert conn.execute("SELECT revision_id FROM app_installations").fetchone() == (payload["release"]["revision_id"],)


def test_start_cannot_overtake_an_accepted_uninstall(device):
    from ringfour_daemon import app_runs

    settings, executor, command, payload, _, _, _ = device
    process(settings, executor, command())
    removal = {key: value for key, value in payload.items() if key != "release"}
    removal.update(expected_revision_id=payload["release"]["revision_id"],
                   expected_configuration_revision_id=payload["release"]["configuration_revision_id"])
    uninstall = command(removal, "uninstall")
    apps.begin_uninstall(settings, executor, uninstall)
    start = next(item["value"] for item in json.loads(
        (ROOT / "fixtures/device/apps/v1/positive.json").read_text()) if item["name"] == "start_command")
    start.update(deployment_namespace=payload["deployment_namespace"], installation_id=payload["installation_id"], run_id=uid())
    for key in ("app_id", "revision_id", "configuration_revision_id", "configuration", "manifest_digest"):
        start["snapshot"][key] = payload["release"][key]
    start["snapshot"]["image"] = payload["release"]["target"]["image"]
    app_runs.begin(settings, executor, command(start, "start"))
    with connect(settings.db_path) as conn:
        state, failure = conn.execute("SELECT state,failure_json FROM app_runs").fetchone()
        assert state == "failed" and json.loads(failure)["code"] == "execution_conflict"
        assert not conn.execute("SELECT 1 FROM device_execution_slots").fetchone()
    apps.finish_uninstall(settings, executor, uninstall)
