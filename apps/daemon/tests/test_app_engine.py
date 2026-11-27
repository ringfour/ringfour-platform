import json
import threading
import uuid

import httpx
import pytest

from ringfour_daemon import app_engine
from ringfour_daemon.config import Settings
from ringfour_daemon.db import connect, migrate


def uid():
    return str(uuid.uuid4())


class FakeEngine:
    def __init__(self):
        self.containers = {}
        self.calls = []
        self.fail = None

    def close(self):
        pass

    def image(self, reference):
        return {
            "Id": "sha256:" + "b" * 64,
            "RepoDigests": [reference],
            "Config": {"Entrypoint": ["python", "/app/main.py"], "User": "10001"},
        }

    def network(self):
        return "network-id"

    def owned(self, robot, namespace):
        return [
            {**c, "Labels": c["Config"]["Labels"]}
            for c in self.containers.values()
            if c["Config"]["Labels"].get("eu.ringfour.robot-id") == robot
            and c["Config"]["Labels"].get("eu.ringfour.deployment-namespace")
            == namespace
        ]

    def container(self, identifier):
        return next(
            (
                c
                for name, c in self.containers.items()
                if name == identifier or c["Id"] == identifier
            ),
            None,
        )

    def output(self, identifier):
        return b"output" * 12000, b"error"

    def request(self, method, path, **kwargs):
        self.calls.append((method, path, kwargs))
        if path == "/containers/create":
            body = kwargs["json"]
            c = {
                "Id": uid().replace("-", ""),
                "Image": body["Image"],
                "HostConfig": body["HostConfig"],
                "Config": body,
                "NetworkSettings": {"Networks": {"apps": {"NetworkID": "network-id"}}},
                "State": {
                    "Status": "created",
                    "Running": False,
                    "StartedAt": "0001-01-01T00:00:00Z",
                },
            }
            self.containers[kwargs["params"]["name"]] = c
        elif path.endswith("/start"):
            c = self.container(path.split("/")[2])
            c["State"].update(
                Status="running", Running=True, StartedAt="2026-11-23T12:00:00Z"
            )
        elif path.endswith("/stop"):
            c = self.container(path.split("/")[2])
            c["State"].update(
                Status="exited",
                Running=False,
                FinishedAt="2026-11-23T12:00:01Z",
                ExitCode=137,
            )
        elif method == "DELETE":
            c = self.container(path.split("/")[2])
            self.containers = {k: v for k, v in self.containers.items() if v is not c}
        if self.fail == path:
            raise app_engine.EngineError()
        return httpx.Response(201)


@pytest.fixture
def setup(tmp_path):
    db = str(tmp_path / "daemon.db")
    migrate(db)
    robot, ns, installation, run, operation = [uid() for _ in range(5)]
    image = "registry.example.com/apps/check@sha256:" + "a" * 64
    with connect(db) as c:
        c.execute(
            "INSERT INTO device_identity VALUES (1,?,?,?,?)",
            (robot, uid(), "http://platform", uid()),
        )
        c.execute(
            "INSERT INTO device_deployment_namespace VALUES (1,?,?)",
            (ns, "2026-11-23T12:00:00Z"),
        )
        c.execute(
            "INSERT INTO device_operations VALUES (?,?,'app.start',1,'{}','accepted',?,?,NULL)",
            (operation, robot, "2099-01-01T00:00:00Z", "2026-11-23T12:00:00Z"),
        )
        c.execute(
            "INSERT INTO app_installations(installation_id,robot_id,app_id,updated_at) VALUES (?,?,?,?)",
            (installation, robot, uid(), "2026-11-23T12:00:00Z"),
        )
        c.execute(
            "INSERT INTO app_runs(run_id,installation_id,robot_id,operation_id,app_id,revision_id,image_digest,configuration_revision_id,snapshot_json,state,requested_at,updated_at) VALUES (?,?,?,?,?,?,?,?,?,'accepted',?,?)",
            (
                run,
                installation,
                robot,
                operation,
                uid(),
                uid(),
                image,
                uid(),
                json.dumps({"image": image, "configuration": {"sample_count": 2}, "configuration_environment": {"SAMPLE_COUNT": "2", "APP_READY": "false"}}),
                "2026-11-23T12:00:00Z",
                "2026-11-23T12:00:00Z",
            ),
        )
    settings = Settings(
        db_path=db,

        apps_enabled=True,
        app_network="apps",
        agent_uri="ws://agent:10101",
    )
    fake = FakeEngine()
    executor = app_engine.Executor(settings, fake).open()
    yield executor, fake, run
    executor.close()


def test_writer_fence_and_replacement_keeps_exact_container_and_no_second_start(setup):
    executor, engine, run = setup
    with pytest.raises(app_engine.EngineError, match="ownership_conflict"):
        app_engine.Executor(executor.settings, FakeEngine()).open()
    row = executor.start(run)
    assert row["state"] == "running"
    original = row["container_id"]
    executor.close()
    replacement = app_engine.Executor(executor.settings, engine).open()
    try:
        assert replacement.start(run)["container_id"] == original
        assert sum(p.endswith("/start") for _, p, _ in engine.calls) == 1
        assert replacement.stop(run)["state"] == "stopped"
        row, _ = replacement.row(run)
        assert row["stdout_sealed"] == row["stderr_sealed"] == 1
        assert row["stdout"] == b"output" * 10922 + b"outp"
        replacement.remove(run)
        assert len(engine.containers) == 0
        assert replacement.start(run)["state"] == "stopped"
    finally:
        replacement.close()


def test_lost_create_reply_adopts_one_and_lost_start_reply_does_not_repeat(setup):
    executor, engine, run = setup
    engine.fail = "/containers/create"
    with pytest.raises(app_engine.EngineError):
        executor.create(run)
    engine.fail = None
    row = executor.create(run)
    assert len(engine.containers) == 1
    assert row["state"] == "accepted"
    engine.fail = "/containers/" + row["container_id"] + "/start"
    with pytest.raises(app_engine.EngineError):
        executor.start(run)
    engine.fail = None
    assert executor.start(run)["state"] == "running"
    assert sum(p.endswith("/start") for _, p, _ in engine.calls) == 1


def test_ambiguous_start_with_created_or_missing_container_is_interrupted(setup):
    executor, engine, run = setup
    executor.create(run)
    executor.phase(run, "start")
    assert executor.start(run)["state"] == "interrupted"
    assert not any(p.endswith("/start") for _, p, _ in engine.calls)


def test_missing_after_confirmed_start_never_relaunches(setup):
    executor, engine, run = setup
    executor.start(run)
    engine.containers.clear()
    result = executor.start(run)
    assert (
        result["state"] == "interrupted"
        and result["exit_code"] is None
        and result["ended_at"] is None
    )
    assert sum(p == "/containers/create" for _, p, _ in engine.calls) == 1


def test_wrong_owner_network_image_or_recorded_id_refuses_mutation(setup):
    executor, engine, run = setup
    row = executor.create(run)
    c = engine.container(row["container_id"])
    for key, new in [("Image", "foreign-image"), ("Id", "foreign-id")]:
        old = c[key]
        c[key] = new
        with pytest.raises(app_engine.EngineError, match="ownership_conflict"):
            executor.start(run)
        c[key] = old
    label = "eu.ringfour.installation-id"
    old = c["Config"]["Labels"][label]
    c["Config"]["Labels"][label] = uid()
    with pytest.raises(app_engine.EngineError, match="ownership_conflict"):
        executor.start(run)
    c["Config"]["Labels"][label] = old
    c["NetworkSettings"]["Networks"]["apps"]["NetworkID"] = "foreign"
    with pytest.raises(app_engine.EngineError, match="ownership_conflict"):
        executor.start(run)
    assert not any(p.endswith(("/start", "/stop")) for _, p, _ in engine.calls)


def test_policy_has_no_entrypoint_bind_privileges_or_credentials(setup):
    executor, engine, run = setup
    executor.create(run)
    body = engine.calls[0][2]["json"]
    host = body["HostConfig"]
    assert "Cmd" not in body and "Entrypoint" not in body
    assert (
        not host.get("Privileged") and not host.get("Binds") and not host.get("PidMode")
    )
    assert host["CapDrop"] == ["ALL"] and host["PidsLimit"] == 128
    assert all("TOKEN" not in e and "SOCKET" not in e for e in body["Env"])
    with pytest.raises(app_engine.EngineError):
        executor.remove(run)


def test_create_injects_only_the_frozen_configuration_environment(setup):
    executor, engine, run = setup
    executor.create(run)
    env = engine.calls[0][2]["json"]["Env"]
    assert "SAMPLE_COUNT=2" in env
    assert "APP_READY=false" in env
    assert "PATH=anything" not in env
    assert "RINGFOUR_APP_CONFIG=false" not in env


def test_real_http_protocol_platform_and_pull_error_stream(tmp_path):
    settings = Settings(db_path=str(tmp_path / "unused"), )
    requests = []

    def handler(request):
        requests.append(request)
        if request.url.path == "/v1.46/info":
            return httpx.Response(
                200, json={"OSType": "linux", "Architecture": "aarch64"}
            )
        return httpx.Response(200, text='{"error":"disk full"}\n')

    engine = app_engine.Engine(settings, httpx.MockTransport(handler))
    try:
        assert engine.platform().architecture == "arm64"
        with pytest.raises(app_engine.EngineError, match="storage_unavailable"):
            engine.pull(
                {
                    "image": "example.com/a@sha256:" + "a" * 64,
                    "os": "linux",
                    "architecture": "arm64",
                },
                {"kind": "bearer", "token": "secret"},
            )
        assert requests[-1].url.path == "/v1.46/images/create"
        assert "secret" not in str(requests[-1].url)
    finally:
        engine.close()


def test_fast_exit_preserves_output_and_terminal_replay(setup):
    executor, engine, run = setup
    row = executor.create(run)
    c = engine.container(row["container_id"])
    c["State"].update(
        Status="exited",
        Running=False,
        StartedAt="2026-11-23T12:00:00Z",
        FinishedAt="2026-11-23T12:00:01Z",
        ExitCode=0,
    )
    result = executor.reconcile(run)
    assert result["state"] == "succeeded" and result["stdout_sealed"] == 1
    assert executor.stop(run)["state"] == "succeeded"
    assert not any(p.endswith(("/stop", "/start")) for _, p, _ in engine.calls)


def test_stop_releases_executor_mutex_while_waiting_for_container(setup):
    executor, engine, run = setup
    executor.start(run)
    stop_entered = threading.Event()
    finish_stop = threading.Event()
    original_request = engine.request

    def delayed_request(method, path, **kwargs):
        if path.endswith("/stop"):
            stop_entered.set()
            assert finish_stop.wait(2)
        return original_request(method, path, **kwargs)

    engine.request = delayed_request
    result = []
    worker = threading.Thread(target=lambda: result.append(executor.stop(run)))
    worker.start()
    assert stop_entered.wait(2)
    # Runtime artifact snapshots take this lock while Docker waits for the App.
    with executor.mutex:
        assert executor.row(run)[0]["state"] == "running"
    finish_stop.set()
    worker.join(2)
    assert not worker.is_alive()
    assert result[0]["state"] == "stopped"


def test_duplicate_owner_matches_and_conflicting_name_are_never_adopted(setup):
    import copy

    executor, engine, run = setup
    row = executor.create(run)
    original = engine.container(row["container_id"])
    duplicate = copy.deepcopy(original)
    duplicate["Id"] = "second"
    engine.containers["extra"] = duplicate
    with pytest.raises(app_engine.EngineError, match="ownership_conflict"):
        executor.start(run)
    engine.containers.pop("extra")
    original["Config"]["Labels"]["eu.ringfour.run-id"] = uid()
    with pytest.raises(app_engine.EngineError, match="ownership_conflict"):
        executor.stop(run)


def test_expired_start_does_not_create_and_log_rotation_preserves_durable_prefix(setup):
    executor,engine,run=setup
    with connect(executor.settings.db_path) as conn:
        conn.execute("UPDATE device_operations SET deadline_at='2000-01-01T00:00:00Z'")
    with pytest.raises(app_engine.EngineError,match='deadline_exceeded'):executor.start(run)
    assert engine.calls==[]
    with connect(executor.settings.db_path) as conn:
        conn.execute("UPDATE device_operations SET deadline_at='2099-01-01T00:00:00Z'")
    original=executor.start(run)
    engine.output=lambda identifier:(b'rotated',b'changed')
    result=executor.reconcile(run)
    assert result['stdout']==original['stdout'] and result['stdout_truncated']==1
    assert result['stderr']==original['stderr'] and result['stderr_truncated']==1


def test_development_snapshot_requires_a_development_image_before_creating_container(setup, monkeypatch):
    executor, engine, run = setup
    original_image = engine.image
    def ordinary_image(reference):
        value = original_image(reference)
        value["Config"]["Labels"] = None
        return value
    monkeypatch.setattr(engine, "image", ordinary_image)
    original_row = executor.row
    def development_row(run_id):
        row, intent = original_row(run_id)
        snapshot = json.loads(row["snapshot_json"])
        snapshot["development"] = {"session_id": uid(), "generation": 1,
            "archive_sha256": "sha256:" + "a" * 64, "expires_at": "2099-01-01T00:00:00Z"}
        return row | {"snapshot_json": json.dumps(snapshot)}, intent
    monkeypatch.setattr(executor, "row", development_row)
    with pytest.raises(app_engine.EngineError,match='revision_unavailable'):
        executor.create(run)
    assert not engine.containers
