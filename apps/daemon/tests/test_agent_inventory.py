import asyncio
import json
import uuid
from pathlib import Path

import httpx
import pytest
import websockets

from ringfour_daemon.agent_inventory import (
    InventoryUnavailable,
    check_fresh,
    engine_platform,
    evaluate,
    observe_agent,
    welcome_observation,
)
from ringfour_daemon.app_inventory import (
    last_successful,
    pending_report,
    queue_report,
    report_once,
)
from ringfour_daemon.app_protocol import Platform
from ringfour_daemon.config import Settings
from ringfour_daemon.db import connect, migrate

ROOT = Path(__file__).resolve().parents[3] / "packages/contracts/fixtures"


def fixture():
    values = json.loads((ROOT / "device/apps/v1/positive.json").read_text())
    return next(row["value"] for row in values if row["name"] == "base101_inventory")


def welcome():
    observation = fixture()["inventory"]["current"]
    return {"jsonrpc": "2.0", "id": 1, "result": {
        "protocol": 1, "profile": {**observation["source"]["profile"], "capabilities": observation["capabilities"]},
        "capabilities": observation["capabilities"], "instances": observation["instances"],
        "features": {}, "description": "<robot/>" * 10000,
    }}


def test_pinned_base101_welcome_and_engine_platform():
    observation = welcome_observation(json.dumps(welcome()))
    assert observation.capabilities == ["mobility", "cameras", "lidar", "imu", "slam", "teleop", "watches", "places", "audit", "status"]
    assert observation.instances == {"cameras": ["base"]}
    assert observation.source.profile.name == "base101"
    assert engine_platform(Settings(db_path="unused", apps_enabled=False,  engine_os="linux", engine_architecture="aarch64")).architecture == "arm64"
    with pytest.raises(InventoryUnavailable):
        engine_platform(Settings(db_path="unused", apps_enabled=False, ))


@pytest.mark.parametrize("change,reason", [
    (lambda v: v.update(id=2), "handshake_invalid"),
    (lambda v: v["result"].update(protocol=2), "unsupported_agent_protocol"),
    (lambda v: v["result"].update(protocol=True), "handshake_invalid"),
    (lambda v: v["result"].update(instances={"arms": ["left"]}), "handshake_invalid"),
    (lambda v: v["result"].update(capabilities=["imu", "imu"]), "handshake_invalid"),
])
def test_invalid_hello_fails_closed(change, reason):
    value = welcome()
    change(value)
    with pytest.raises(InventoryUnavailable) as error:
        welcome_observation(json.dumps(value))
    assert error.value.reason == reason


def test_real_websocket_hello_uses_only_read_protocol_and_times_out():
    async def run():
        calls = []
        async def handler(socket):
            request = json.loads(await socket.recv())
            calls.append(request)
            await socket.send(json.dumps(welcome()))
        async with websockets.serve(handler, "127.0.0.1", 0) as server:
            uri = "ws://127.0.0.1:" + str(server.sockets[0].getsockname()[1])
            observation = await observe_agent(uri)
            assert "imu" in observation.capabilities
        assert len(calls) == 1 and calls[0]["method"] == "hello"
        assert calls[0]["params"]["protocol"] == 1

        async def silent(socket):
            await socket.wait_closed()
        async with websockets.serve(silent, "127.0.0.1", 0) as server:
            uri = "ws://127.0.0.1:" + str(server.sockets[0].getsockname()[1])
            with pytest.raises(InventoryUnavailable) as error:
                await observe_agent(uri, timeout=0.05)
            assert error.value.reason == "agent_unreachable"
    asyncio.run(run())


def test_capability_checks_missing_optional_arm_target_and_native():
    manifest = json.loads((ROOT / "apps/v1/manifest.json").read_text())
    observation = welcome_observation(json.dumps(welcome()))
    platform = Platform.model_validate(fixture()["platform"])
    assert evaluate(manifest, observation, platform)["status"] == "compatible"
    observation.capabilities.remove("cameras")
    result = evaluate(manifest, observation, platform)
    assert result["status"] == "compatible" and result["missing_optional"] == [{"capability": "cameras"}]
    observation.capabilities.remove("imu")
    assert evaluate(manifest, observation, platform)["status"] == "missing_required"
    platform.architecture = "arm64"
    assert evaluate(manifest, observation, platform)["status"] == "no_matching_image"
    platform.architecture = "amd64"
    manifest["capabilities"]["required"] = [{"capability": "groups", "instance_kind": "arm"}]
    observation.capabilities.append("groups")
    observation.instances["groups"] = ["head"]
    assert evaluate(manifest, observation, platform)["status"] == "missing_required"
    observation.instances["arms"] = ["head"]
    assert evaluate(manifest, observation, platform)["status"] == "compatible"
    manifest["targets"][0]["native_requirements"] = ["cuda:12"]
    assert evaluate(manifest, observation, platform)["status"] == "native_requirement_unavailable"


def enrolled(tmp_path):
    db = str(tmp_path / "daemon.db")
    migrate(db)
    namespace = str(uuid.uuid4())
    with connect(db) as conn:
        conn.execute("INSERT INTO device_identity VALUES (1,'robot','org','http://platform','credential')")
        conn.execute("INSERT INTO device_deployment_namespace VALUES (1,?, '2026-11-23T10:00:00Z')", (namespace,))
    return db, {"deployment_namespace": namespace}


def test_durable_report_retries_same_payload_before_new_observation(tmp_path):
    db, identity = enrolled(tmp_path)
    report = fixture()
    first = queue_report(db, identity["deployment_namespace"], Platform.model_validate(report["platform"]), report["inventory"])
    assert pending_report(db) == first
    assert last_successful(db) == report["inventory"]["current"]
    requests = []
    def handler(request):
        requests.append(json.loads(request.content))
        if len(requests) == 1:
            return httpx.Response(503)
        return httpx.Response(200, json={"report_id": first["report_id"], "report_sequence": 1, "received_at": "2026-11-23T10:01:00Z"})
    settings = Settings(db_path=db,  agent_uri="unix:///must-not-be-read")
    async def run():
        async with httpx.AsyncClient(base_url="http://platform", transport=httpx.MockTransport(handler)) as client:
            with pytest.raises(httpx.HTTPStatusError):
                await report_once(settings, identity, client, Platform.model_validate(report["platform"]))
            await report_once(settings, identity, client, Platform.model_validate(report["platform"]))
    asyncio.run(run())
    assert requests == [first, first]
    assert pending_report(db) is None


def test_fresh_gate_does_not_use_cached_inventory(tmp_path):
    settings = Settings(db_path="unused", apps_enabled=False,  agent_uri="unix://" + str(tmp_path / "missing.sock"),
                        engine_os="linux", engine_architecture="x86_64")
    manifest = json.loads((ROOT / "apps/v1/manifest.json").read_text())
    assert asyncio.run(check_fresh(settings, manifest))["status"] == "inventory_unavailable"
