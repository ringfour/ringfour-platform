"""AP-01 typed readers only: these tests do not exercise network or Docker."""

import json
from pathlib import Path

import pytest
from jsonschema import ValidationError
from ringfour_daemon import app_validation
from ringfour_daemon.app_protocol import (
    InventoryReport,
    NegotiationRequest,
    NegotiationResponse,
    ProtocolError,
    RobotAppStatus,
    ownership_labels,
    parse,
    select_version,
    validate_selection,
)

FIXTURES = (
    Path(__file__).resolve().parents[3] / "packages/contracts/fixtures/device/apps/v1"
)
POSITIVE = json.loads((FIXTURES / "positive.json").read_text())
MODELS = {
    "negotiation_request": NegotiationRequest,
    "negotiation_response": NegotiationResponse,
    "inventory_report": InventoryReport,
    "robot_app_status": RobotAppStatus,
}


def fixture(name):
    return next(f["value"] for f in POSITIVE if f["name"] == name)


@pytest.mark.parametrize(
    "example",
    [f for f in POSITIVE if f["schema"].split("/")[-1] in MODELS],
    ids=lambda f: f["name"],
)
def test_positive_fixtures(example):
    model = MODELS[example["schema"].split("/")[-1]]
    assert parse(model, json.dumps(example["value"])).model_dump() == example["value"]


@pytest.mark.parametrize(
    "example",
    [
        f
        for f in json.loads((FIXTURES / "rejected.json").read_text())
        if f["schema"].split("/")[-1] in MODELS
    ],
    ids=lambda f: f["name"],
)
def test_rejected_fixtures(example):
    with pytest.raises(ProtocolError):
        parse(MODELS[example["schema"].split("/")[-1]], json.dumps(example["value"]))


def test_negotiation_is_bound_to_session_namespace_and_offer():
    offer = parse(NegotiationRequest, json.dumps(fixture("negotiation_offer")))
    selected = parse(NegotiationResponse, json.dumps(fixture("negotiation_selected")))
    assert select_version(offer) == 1
    validate_selection(offer, selected)
    for field in ("session_id", "deployment_namespace"):
        wrong = selected.model_copy(
            update={field: "ffffffff-ffff-4fff-8fff-ffffffffffff"}
        )
        with pytest.raises(ProtocolError):
            validate_selection(offer, wrong)
    unsupported = parse(
        NegotiationRequest,
        json.dumps(
            {**fixture("negotiation_offer"), "supported_app_protocol_versions": [2]}
        ),
    )
    with pytest.raises(ProtocolError, match="daemon_upgrade_required"):
        select_version(unsupported)


def test_negotiation_feature_offers_default_false_and_remain_explicit():
    offer = parse(NegotiationRequest, json.dumps(fixture("negotiation_offer")))
    assert not offer.supports_manifest_v2
    assert not offer.supports_live_settings
    updated = parse(
        NegotiationRequest,
        json.dumps(
            {
                **fixture("negotiation_offer"),
                "supports_manifest_v2": True,
                "supports_live_settings": True,
            }
        ),
    )
    assert updated.supports_manifest_v2 and updated.supports_live_settings


def test_manifest_v2_environment_mapping_is_validated_and_scalar():
    contracts = (
        Path(__file__).resolve().parents[3] / "packages/contracts/fixtures/apps/v2"
    )
    manifest = json.loads((contracts / "manifest.json").read_text())
    app_validation.validate_manifest(manifest)
    canonical = json.loads((contracts / "canonical.json").read_text())
    assert app_validation.canonical(manifest) == canonical["json"]
    assert app_validation.digest(manifest) == canonical["digest"]
    values = manifest["configuration"]["defaults"]
    assert app_validation.configuration_environment(manifest, values) == {
        "SAMPLE_COUNT": "3"
    }
    manifest["configuration"]["environment"] = {"PATH": "sample_count"}
    with pytest.raises(ValidationError, match="PATH"):
        app_validation.validate_manifest(manifest)
    manifest = json.loads((contracts / "manifest.json").read_text())
    manifest["configuration"]["schema"]["properties"]["note"] = {"type": "string"}
    manifest["configuration"]["environment"]["APP_NOTE"] = "note"
    with pytest.raises(ValueError, match="NUL"):
        app_validation.configuration_environment(
            manifest, {"sample_count": 3, "note": "bad\0value"}
        )


def test_bounded_float_manifest_environment_settings_and_canonical_roundtrip():
    contracts = (
        Path(__file__).resolve().parents[3] / "packages/contracts/fixtures/apps/v2"
    )
    fixture = json.loads((contracts / "floating-point.json").read_text())
    manifest = app_validation.validate_manifest(fixture["manifest"])
    assert app_validation.digest(manifest) == fixture["canonical_digest"]
    values = app_validation.raw_json(json.dumps(fixture["configuration_values"]))
    app_validation.validate_configuration(manifest, values)
    assert (
        app_validation.configuration_environment(manifest, values)
        == fixture["expected_environment"]
    )
    settings = app_validation.raw_json(json.dumps(fixture["runtime_settings_values"]))
    assert app_validation.canonical(settings) == app_validation.canonical(
        fixture["runtime_settings_values"]
    )
    assert (
        app_validation.canonical({"a": 0.000001, "b": 0.0000001, "c": -0.125})
        == '{"a":0.000001,"b":1e-7,"c":-0.125}'
    )

    for raw in (
        '{"value":NaN}',
        '{"value":Infinity}',
        '{"value":1e400}',
        '{"value":1e-400}',
        '{"value":9007199254740992}',
    ):
        with pytest.raises(ValueError):
            app_validation.raw_json(raw)


@pytest.mark.parametrize("value", [True, "1", 1.5, 0, 2])
def test_versions_are_strict_json_integers(value):
    with pytest.raises(ProtocolError):
        parse(
            InventoryReport,
            json.dumps({**fixture("base101_inventory"), "app_protocol_version": value}),
        )


def test_unavailable_and_offline_keep_cache_explanatory():
    unavailable = parse(
        RobotAppStatus, json.dumps(fixture("agent_unavailable_with_cache"))
    )
    assert unavailable.inventory.state == "unavailable"
    assert not hasattr(unavailable.inventory, "current")
    assert unavailable.inventory.last_successful.source.profile.name == "base101"
    offline = parse(RobotAppStatus, json.dumps(fixture("offline_last_observation")))
    assert offline.connectivity == "offline"
    assert (
        offline.inventory.current.observed_at
        == fixture("online_status")["inventory"]["current"]["observed_at"]
    )


def test_raw_json_refuses_duplicates_non_json_numbers_and_unknown_members():
    raw = json.dumps(fixture("negotiation_offer"))
    with pytest.raises(ProtocolError):
        parse(NegotiationRequest, raw[:-1] + ', "session_id": "ignored"}')
    with pytest.raises(ProtocolError):
        parse(NegotiationRequest, raw[:-1] + ', "unknown": NaN}')
    with pytest.raises(ProtocolError):
        parse(NegotiationRequest, raw[:-1] + ', "unknown": null}')
    with pytest.raises(ProtocolError):
        parse(NegotiationRequest, " " * 65536 + raw)


def test_arms_require_declared_groups_and_names_are_not_identity():
    value = json.loads(json.dumps(fixture("base101_inventory")))
    value["inventory"]["current"]["instances"]["arms"] = ["left"]
    with pytest.raises(ProtocolError):
        parse(InventoryReport, json.dumps(value))
    owner = fixture("owner")
    labels = ownership_labels(**owner)
    assert labels["eu.ringfour.robot-id"] == owner["robot_id"]
    assert labels["eu.ringfour.deployment-namespace"] == owner["deployment_namespace"]
    with pytest.raises(ProtocolError):
        ownership_labels(**{**owner, "robot_id": "base101"})


def test_integral_json_number_representation_matches_json_schema():
    value = {**fixture("base101_inventory"), "app_protocol_version": 1.0}
    assert parse(InventoryReport, json.dumps(value)).app_protocol_version == 1
    value["app_protocol_version"] = 1e30
    with pytest.raises(ProtocolError):
        parse(InventoryReport, json.dumps(value))
