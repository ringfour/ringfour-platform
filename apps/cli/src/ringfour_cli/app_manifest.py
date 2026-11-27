"""Independent CLI implementation of App manifest and authoring validation."""

import copy
import hashlib
import json
import math
import re
from decimal import Decimal, DecimalException
from importlib.resources import files
from pathlib import PurePosixPath

from jsonschema import Draft202012Validator
from jsonschema.exceptions import ValidationError

MAX_INTEGER = 9007199254740991
MAX_NUMBER = MAX_INTEGER


def _parse_number(token):
    try:
        exact = Decimal(token)
        value = float(exact)
        if not exact.is_finite() or abs(exact) > MAX_NUMBER or not math.isfinite(value):
            raise ValueError
        if exact != 0 and value == 0:
            raise ValueError
        return int(value) if value.is_integer() and abs(value) <= MAX_NUMBER else value
    except (DecimalException, OverflowError) as error:
        raise ValueError(
            "App number is outside the finite safe numeric range"
        ) from error


def _number_text(value):
    if type(value) is int:
        if abs(value) > MAX_NUMBER:
            raise ValueError("App number is outside the safe numeric range")
        return str(value)
    if type(value) is not float or not math.isfinite(value) or abs(value) > MAX_NUMBER:
        raise ValueError("App number is outside the finite safe numeric range")
    if value == 0:
        return "0"
    if value.is_integer():
        return str(int(value))
    raw = repr(value).lower()
    sign = "-" if raw.startswith("-") else ""
    raw = raw.lstrip("-")
    mantissa, _, exponent = raw.partition("e")
    power = int(exponent) if exponent else 0
    point = mantissa.find(".")
    position = (len(mantissa) if point < 0 else point) + power
    digits = mantissa.replace(".", "")
    leading = len(digits) - len(digits.lstrip("0"))
    digits = digits[leading:]
    position -= leading
    digits = digits.rstrip("0") or "0"
    if 0 < position <= 21:
        rendered = (
            digits + "0" * (position - len(digits))
            if len(digits) <= position
            else digits[:position] + "." + digits[position:]
        )
    elif -6 < position <= 0:
        rendered = "0." + "0" * (-position) + digits
    else:
        exponent = position - 1
        rendered = (
            digits[0]
            + ("." + digits[1:] if len(digits) > 1 else "")
            + "e"
            + ("+" if exponent >= 0 else "")
            + str(exponent)
        )
    return sign + rendered


def canonical(value):
    if isinstance(value, dict):
        return (
            "{"
            + ",".join(
                json.dumps(key, ensure_ascii=False) + ":" + canonical(value[key])
                for key in sorted(value)
            )
            + "}"
        )
    if isinstance(value, list):
        return "[" + ",".join(canonical(item) for item in value) + "]"
    if type(value) in (int, float):
        return _number_text(value)
    return json.dumps(value, ensure_ascii=False, allow_nan=False, separators=(",", ":"))


def fingerprint(value):
    return hashlib.sha256(canonical(value).encode()).hexdigest()


def _unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate JSON member")
        result[key] = value
    return result


def _scalars(value):
    if isinstance(value, str):
        value.encode("utf-8", errors="strict")
    elif isinstance(value, dict):
        return {_scalars(key): _scalars(item) for key, item in value.items()}
    elif isinstance(value, list):
        return [_scalars(item) for item in value]
    elif type(value) is int:
        if abs(value) > MAX_INTEGER:
            raise ValueError("Only safe JSON integers are supported")
    elif type(value) is float:
        if not math.isfinite(value) or abs(value) > MAX_NUMBER:
            raise ValueError("App number is outside the finite safe numeric range")
        if value.is_integer():
            return int(value)
    return value


def read_json(path):
    try:
        with path.open("rb") as stream:
            raw = stream.read(65537)
        if len(raw) > 65536:
            raise ValueError("App JSON exceeds 65536 bytes")
        return _scalars(
            json.loads(
                raw.decode("utf-8"),
                object_pairs_hook=_unique,
                parse_float=_parse_number,
            )
        )
    except (UnicodeError, json.JSONDecodeError, RecursionError) as error:
        raise ValueError("Invalid App UTF-8 JSON") from error


def _structure(value, name):
    version_suffix = (
        "-v2" if isinstance(value, dict) and value.get("schema_version") == 2 else ""
    )
    schema = json.loads(
        files("ringfour_cli")
        .joinpath("data", name + version_suffix + ".schema.json")
        .read_text()
    )
    try:
        Draft202012Validator(schema).validate(value)
    except ValidationError as error:
        raise ValueError(
            f"Invalid {name} at {'/'.join(map(str, error.absolute_path))}: {error.message[:300]}"
        ) from error


def validate_manifest(value):
    value = _scalars(value)
    if len(canonical(value).encode()) > 65536:
        raise ValueError("Manifest exceeds 65536 bytes")
    _structure(value, "manifest")
    platforms = [(target["os"], target["architecture"]) for target in value["targets"]]
    if len(set(platforms)) != len(platforms):
        raise ValueError("Image target platforms must be unique")
    required = {canonical(predicate) for predicate in value["capabilities"]["required"]}
    if any(
        canonical(predicate) in required
        for predicate in value["capabilities"]["optional"]
    ):
        raise ValueError("Required and optional capabilities overlap")
    _validate_configuration(value["configuration"], "Configuration")
    if value["schema_version"] == 2 and "runtime_settings" in value:
        _validate_configuration(value["runtime_settings"], "Runtime settings")
    return value


def _validate_configuration(configuration, label):
    schema = copy.deepcopy(configuration["schema"])
    if any(key not in schema["properties"] for key in schema["required"]):
        raise ValueError(f"Required {label.lower()} key is not declared")
    for rule in schema["properties"].values():
        if rule.get("minimum", -MAX_INTEGER) > rule.get(
            "maximum", MAX_INTEGER
        ) or rule.get("minLength", 0) > rule.get("maxLength", 4096):
            raise ValueError(f"{label} bounds are reversed")
        if rule["type"] == "string":
            rule.setdefault("maxLength", 4096)
        if rule["type"] == "integer":
            rule.setdefault("minimum", -MAX_INTEGER)
            rule.setdefault("maximum", MAX_INTEGER)
        if rule["type"] == "number":
            rule.setdefault("minimum", -MAX_NUMBER)
            rule.setdefault("maximum", MAX_NUMBER)
        if "enum" in rule:
            bounds = {key: item for key, item in rule.items() if key != "enum"}
            if any(
                not Draft202012Validator(bounds).is_valid(choice)
                for choice in rule["enum"]
            ):
                raise ValueError(f"{label} enum violates its bounds/type")
    defaults = configuration["defaults"]
    if len(canonical(defaults).encode()) > 16384 or not Draft202012Validator(
        schema
    ).is_valid(defaults):
        raise ValueError(f"{label} defaults violate schema or byte limits")


def safe_source_path(name):
    path = PurePosixPath(name)
    if (
        not isinstance(name, str)
        or path.is_absolute()
        or not re.fullmatch(r"[A-Za-z0-9_.][A-Za-z0-9_./-]*", name)
        or any(part in ("", ".", "..") for part in name.split("/"))
    ):
        raise ValueError("Build inputs require safe relative file paths")
    for part in path.parts:
        if (
            (part.startswith(".") and name != ".dockerignore")
            or part.endswith((".context.json", ".auth.env"))
            or part
            in (
                "credentials.json",
                "token.json",
                "id_rsa",
                "id_ed25519",
                "app.json",
                "app.manifest.json",
                "python-wheels",
            )
        ):
            raise ValueError(
                "Credentials, selections and generated files cannot be build inputs"
            )
    return path


def validate_authoring(value):
    value = _scalars(value)
    _structure(value, "authoring")
    source = copy.deepcopy(value)
    source.pop("build")
    source.pop("wheel_bundle")
    for target in source["targets"]:
        target["image"] = target.pop("repository") + "@sha256:" + "0" * 64
    validate_manifest(source)  # Placeholder exists only inside semantic validation.
    for name in [value["build"]["dockerfile"], *value["build"]["files"]]:
        safe_source_path(name)
    return value
