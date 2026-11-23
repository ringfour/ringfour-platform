"""OpenAPI reference and App contract checks; requires requirements-test.txt."""
import json
from functools import cache
import re
import unittest
from pathlib import Path
from urllib.parse import unquote, urlparse

import yaml

ROOT = Path(__file__).resolve().parents[1]


class UniqueKeyLoader(yaml.SafeLoader):
    """Reject duplicate mapping keys instead of silently dropping a route."""


def unique_mapping(loader, node):
    result = {}
    for key_node, value_node in node.value:
        key = loader.construct_object(key_node)
        if key in result:
            raise ValueError(f"duplicate YAML key: {key}")
        result[key] = loader.construct_object(value_node)
    return result


UniqueKeyLoader.add_constructor(
    yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, unique_mapping
)


@cache
def load(path):
    text = path.read_text()
    return json.loads(text) if path.suffix == ".json" else yaml.load(text, Loader=UniqueKeyLoader)


def walk(value):
    if isinstance(value, dict):
        yield value
        for item in value.values():
            yield from walk(item)
    elif isinstance(value, list):
        for item in value:
            yield from walk(item)


def resolve(source, reference):
    target, _, fragment = reference.partition("#")
    if target.startswith("https://contracts.ringfour.dev/"):
        path = ROOT / "schemas" / target.removeprefix("https://contracts.ringfour.dev/")
    elif urlparse(target).scheme:
        raise ValueError(f"unapproved remote reference: {reference}")
    else:
        path = source.parent / target if target else source
    path = path.resolve()
    if not path.is_relative_to(ROOT):
        raise ValueError(f"reference outside contracts: {reference}")
    value = load(path)
    if fragment:
        if not fragment.startswith("/"):
            raise ValueError(f"unsupported JSON pointer: {reference}")
        for part in fragment[1:].split("/"):
            value = value[unquote(part).replace("~1", "/").replace("~0", "~")]
    return value


class OpenApiContracts(unittest.TestCase):
    def test_all_document_and_schema_references_resolve(self):
        references = 0
        for path in [*ROOT.glob("openapi/*.yaml"), *ROOT.glob("schemas/**/*.json")]:
            document = load(path)
            for node in walk(document):
                if "$ref" in node:
                    with self.subTest(source=path.relative_to(ROOT), ref=node["$ref"]):
                        self.assertIsInstance(resolve(path, node["$ref"]), (dict, list))
                    references += 1
        self.assertGreater(references, 200)

    def test_duplicate_yaml_keys_rejected(self):
        with self.assertRaisesRegex(ValueError, "duplicate YAML key"):
            yaml.load("paths:\n  /apps: {}\n  /apps: {}", Loader=UniqueKeyLoader)

    def test_bad_pointer_and_remote_reference_rejected(self):
        source = ROOT / "openapi/ringfour-api.v1.yaml"
        with self.assertRaises(KeyError):
            resolve(source, "../schemas/apps/v1/api.schema.json#/$defs/does_not_exist")
        with self.assertRaisesRegex(ValueError, "unapproved remote"):
            resolve(source, "https://example.com/schema.json")

    def test_proposed_app_paths_have_auth_scopes_parameters_and_errors(self):
        count = 0
        for name in ["ringfour-api.v1.yaml", "ringfour-runtime.v1.yaml"]:
            source = ROOT / "openapi" / name
            document = load(source)
            for path, item in document["paths"].items():
                for method, operation in item.items():
                    if not isinstance(operation, dict) or operation.get("x-implementation-status") != "contract-only":
                        continue
                    count += 1
                    with self.subTest(path=path, method=method):
                        parameters = item.get("parameters", []) + operation.get("parameters", [])
                        parameters = [resolve(source, p["$ref"]) if "$ref" in p else p for p in parameters]
                        names = {p["name"] for p in parameters if p["in"] == "path" and p.get("required")}
                        self.assertEqual(names, set(re.findall(r"{(.*?)}", path)))
                        self.assertTrue(operation.get("security", document.get("security")))
                        if name == "ringfour-api.v1.yaml":
                            if path.startswith("/v1/device/"):
                                self.assertEqual(operation["security"], [{"deviceCredentialId": [], "deviceCredentialSecret": []}])
                                names = {p["name"] for p in parameters if p["in"] == "header" and p.get("required")}
                                self.assertTrue({"x-ringfour-credential-id", "x-ringfour-credential-secret"} <= names)
                                if not path.endswith("/negotiate"):
                                    self.assertIn("x-ringfour-device-session", names)
                            else:
                                self.assertEqual(operation.get("security", document.get("security")), [{"bearerAuth": []}])
                                if method == "post":
                                    self.assertTrue(any(p["name"] == "Idempotency-Key" and p.get("required") for p in parameters))
                        else:
                            self.assertEqual(operation["security"], [{"AppRunCredential": []}])
                        for code in ["400", "401", "404", "409", "503"]:
                            ref = operation["responses"][code]["content"]["application/json"]["schema"]["$ref"]
                            self.assertEqual(ref, "../schemas/errors/v2/app-error.schema.json")
        self.assertGreaterEqual(count, 30)

    def test_operation_ids_unique_and_legacy_namespaces_retained(self):
        for path in ROOT.glob("openapi/*.yaml"):
            document = load(path)
            ids = [node["operationId"] for node in walk(document) if "operationId" in node]
            self.assertEqual(len(ids), len(set(ids)), path)
        api = load(ROOT / "openapi/ringfour-api.v1.yaml")
        runtime = load(ROOT / "openapi/ringfour-runtime.v1.yaml")
        self.assertIn("/v1/executions/{execution_id}/output", api["paths"])
        self.assertIn("/v1/runtime/interactions", runtime["paths"])
        self.assertIn("/v1/app-runtime/interactions", runtime["paths"])


if __name__ == "__main__":
    unittest.main()
