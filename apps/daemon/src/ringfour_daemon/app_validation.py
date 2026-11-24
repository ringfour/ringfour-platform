"""Independent public-schema consumer for installation commands and manifests."""

import hashlib
import json
from importlib.resources import files

from jsonschema import Draft202012Validator, FormatChecker
from referencing import Registry, Resource

from .app_protocol import (
    _integer_number,
    _non_json_number,
    _unique_object,
    _validate_scalars,
)

SCHEMAS = {
    name: json.loads(
        files("ringfour_daemon").joinpath("app_schemas", name + ".json").read_text()
    )
    for name in ("manifest", "api", "device", "error")
}
REGISTRY = Registry().with_resources(
    (value["$id"], Resource.from_contents(value)) for value in SCHEMAS.values()
)


def raw_json(raw):
    if len(raw.encode()) > 70000:
        raise ValueError("App response exceeds byte limit")
    value = json.loads(
        raw,
        object_pairs_hook=_unique_object,
        parse_int=_integer_number,
        parse_float=_integer_number,
        parse_constant=_non_json_number,
    )
    _validate_scalars(value)
    return value


def canonical(value):
    return json.dumps(
        value,
        sort_keys=True,
        ensure_ascii=False,
        separators=(",", ":"),
        allow_nan=False,
    )


def digest(value):
    return "sha256:" + hashlib.sha256(canonical(value).encode()).hexdigest()


def validate(value, schema, definition=None):
    reference = SCHEMAS[schema]["$id"] + (
        "" if definition is None else "#/$defs/" + definition
    )
    Draft202012Validator(
        {"$ref": reference}, registry=REGISTRY, format_checker=FormatChecker()
    ).validate(value)
    if len(canonical(value).encode()) > 65536:
        raise ValueError("App body exceeds byte limit")
    return value


def validate_configuration(manifest, values):
    if len(canonical(values).encode()) > 16384:
        raise ValueError("Configuration exceeds byte limit")
    validate(values, "api", "configuration")
    Draft202012Validator(manifest["configuration"]["schema"]).validate(values)
    return values
