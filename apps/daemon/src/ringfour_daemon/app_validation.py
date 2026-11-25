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
    for name in ("manifest", "manifest_v2", "api", "device", "error")
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


def validate_manifest(manifest):
    version = manifest.get("schema_version") if isinstance(manifest, dict) else None
    if version not in (1, 2):
        raise ValueError("Unsupported App manifest version")
    validate(manifest, "manifest" if version == 1 else "manifest_v2")
    if version == 2:
        mappings = manifest["configuration"].get("environment", {})
        if len(mappings) > 32:
            raise ValueError("Environment mapping exceeds entry limit")
        reserved = {"PATH", "IFS", "ENV", "BASH_ENV", "BASHOPTS", "SHELLOPTS", "NODE_OPTIONS", "NODE_PATH", "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "RUBYOPT", "RUBYLIB", "PERL5OPT", "PERL5LIB"}
        properties = manifest["configuration"]["schema"]["properties"]
        for name, key in mappings.items():
            if (name.startswith(("RINGFOUR_", "LD_", "DYLD_", "PYTHON")) or name in reserved or key not in properties):
                raise ValueError("Invalid or reserved App environment mapping")
        configuration_environment(manifest, manifest["configuration"]["defaults"])
        runtime = manifest.get("runtime_settings")
        if runtime is not None:
            schema = runtime["schema"]
            Draft202012Validator(schema).validate(runtime["defaults"])
            if len(canonical(runtime["defaults"]).encode()) > 16384:
                raise ValueError("Runtime settings exceed byte limit")
    return manifest


def configuration_environment(manifest, values):
    output = {}
    for name, key in manifest["configuration"].get("environment", {}).items():
        if key not in values:
            continue
        value = values[key]
        if isinstance(value, bool):
            output[name] = "true" if value else "false"
        elif isinstance(value, str):
            if "\0" in value:
                raise ValueError("Mapped App environment value contains NUL")
            output[name] = value
        elif isinstance(value, int):
            output[name] = str(value)
        else:
            raise ValueError("App environment values must be scalar")  # noqa: TRY004 -- public validation uses ValueError
    size = sum(len(name.encode()) + len(value.encode()) + 1 for name, value in output.items())
    if size > 16384:
        raise ValueError("Mapped App environment exceeds byte limit")
    return output
