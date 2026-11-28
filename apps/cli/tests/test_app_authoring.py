import copy
import gzip
import hashlib
import io
import json
import tarfile
from pathlib import Path

import httpx
import pytest
from typer.testing import CliRunner

from ringfour_cli import app_project, main
from ringfour_cli.app_manifest import (
    fingerprint,
    read_json,
    validate_authoring,
    validate_manifest,
)
from ringfour_cli.config import Settings

CONTRACTS = Path(__file__).resolve().parents[3] / "packages/contracts"
ORG = "11111111-1111-4111-8111-111111111111"


@pytest.fixture
def project(tmp_path, monkeypatch):
    manifest = json.loads((CONTRACTS / "fixtures/apps/v1/manifest.json").read_text())
    for target in manifest["targets"]:
        target["repository"] = "registry.example.com/team/check"
        target.pop("image")
    manifest["build"] = {"dockerfile": "Dockerfile", "files": ["main.py"]}
    bundle = tmp_path / "bundle"
    (bundle / "linux-amd64/wheels").mkdir(parents=True)
    (bundle / "release.json").write_text('{"bundle_version":"test"}')
    for name, text in [
        ("linux-amd64/requirements.txt", ""),
        ("linux-amd64/wheels/test.whl", "fixture"),
    ]:
        (bundle / name).write_text(text)
    sums = "".join(
        f"{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.relative_to(bundle)}\n"
        for p in sorted(bundle.rglob("*"))
        if p.is_file()
    )
    (bundle / "SHA256SUMS").write_text(sums)
    manifest["wheel_bundle"] = {
        "version": "test",
        "sha256": hashlib.sha256(sums.encode()).hexdigest(),
    }
    root = tmp_path / "app"
    root.mkdir()
    (root / "app.json").write_text(json.dumps(manifest))
    (root / "Dockerfile").write_text('FROM scratch\nENTRYPOINT ["/main"]\n')
    (root / "main.py").write_text("print('fixture')")
    (root / ".env").write_text("DO_NOT_SHIP=credential")
    (root / "ringfour.context.json").write_text("private-selection")
    calls = []
    state = {
        "id": "sha256:" + "1" * 64,
        "raw": b'{"schemaVersion":2,"mediaType":"application/vnd.oci.image.manifest.v1+json","config":{},"layers":[]}',
    }

    def docker(*args):
        calls.append(args)
        if args[:2] == ("buildx", "build"):
            context = Path(args[-1])
            assert not (context / ".env").exists()
            assert not (context / "ringfour.context.json").exists()
            assert not (context / ".ringfour").exists()
            assert (context / "main.py").read_text() == (root / "main.py").read_text()
            return b""
        if args[:2] == ("image", "inspect"):
            return json.dumps(
                {"Id": state["id"], "Os": "linux", "Architecture": "amd64"}
            ).encode()
        if args[:2] in (("image", "tag"), ("image", "push")):
            return b""
        if args[:3] == ("buildx", "imagetools", "inspect"):
            if args[-1] == "--raw":
                return state["raw"] + b"\n"
            if args[-1] == "{{json .Image}}":
                return b'{"os":"linux","architecture":"amd64"}'
            return json.dumps(
                {
                    "mediaType": state.get(
                        "type", "application/vnd.oci.image.manifest.v1+json"
                    ),
                    "digest": "sha256:" + hashlib.sha256(state["raw"]).hexdigest(),
                }
            ).encode()
        raise AssertionError(args)

    monkeypatch.setattr(app_project, "docker", docker)
    return root, bundle, calls, state


def test_independent_validator_matches_manifest_semantics_and_canonical_digest():
    value = json.loads((CONTRACTS / "fixtures/apps/v1/manifest.json").read_text())
    assert (
        "sha256:" + fingerprint(validate_manifest(value))
        == json.loads((CONTRACTS / "fixtures/apps/v1/canonical.json").read_text())[
            "digest"
        ]
    )
    for fixture in json.loads(
        (CONTRACTS / "fixtures/apps/v1/rejected.json").read_text()
    ):
        with pytest.raises(ValueError):
            validate_manifest(fixture["manifest"])
    for name in ("manifest", "authoring"):
        assert (
            Path(main.__file__).parent / f"data/{name}.schema.json"
        ).read_bytes() == (
            CONTRACTS / f"schemas/apps/v1/{name}.schema.json"
        ).read_bytes()
        assert (
            Path(main.__file__).parent / f"data/{name}-v2.schema.json"
        ).read_bytes() == (
            CONTRACTS / f"schemas/apps/v2/{name}.schema.json"
        ).read_bytes()


def test_v2_authoring_and_manifest_validate_with_environment_and_live_settings():
    authoring = json.loads((CONTRACTS / "fixtures/apps/v2/authoring.json").read_text())
    manifest = json.loads((CONTRACTS / "fixtures/apps/v2/manifest.json").read_text())
    assert validate_authoring(authoring) == authoring
    assert validate_manifest(manifest) == manifest

    with_toolchain = copy.deepcopy(authoring)
    with_toolchain["toolchain"] = {
        "protocol": 1,
        "sdk": {"version": "1.0.0", "sha256": "a" * 64},
        "agent": {"min_version": "1.0.0"},
    }
    assert validate_authoring(with_toolchain) == with_toolchain
    with_toolchain["toolchain"]["sdk"]["sha256"] = "invalid"
    with pytest.raises(ValueError):
        validate_authoring(with_toolchain)

    invalid = copy.deepcopy(authoring)
    invalid["runtime_settings"]["defaults"]["interval_ms"] = "250"
    with pytest.raises(ValueError):
        validate_authoring(invalid)


def test_toolchain_build_requires_explicit_dockerfile_install(project):
    root, bundle, calls, _state = project
    document = read_json(root / "app.json")
    document["schema_version"] = 2
    document["toolchain"] = {"protocol": 1, "sdk": {"version": "1.0.0", "sha256": "a" * 64}}
    (root / "app.json").write_text(json.dumps(document))
    with pytest.raises(ValueError, match="must COPY"):
        app_project.build_project(root, bundle)
    assert not calls


def test_toolchain_build_stages_pinned_wheel_and_checks_image(project, monkeypatch, tmp_path):
    root, bundle, _calls, _state = project
    document = read_json(root / "app.json")
    document["schema_version"] = 2
    document["toolchain"] = {"protocol": 1, "sdk": {"version": "1.0.0", "sha256": "a" * 64}}
    (root / "app.json").write_text(json.dumps(document))
    (root / "Dockerfile").write_text("FROM scratch\nCOPY sdk-wheel/*.whl /tmp/sdk-wheel/\n")
    wheel = tmp_path / "ringfour_sdk-1.0.0-py3-none-any.whl"
    wheel.write_bytes(b"sdk bytes")
    monkeypatch.setattr(app_project.sdk_release, "fetch", lambda *_: wheel)
    original_docker = app_project.docker
    staged = []

    def docker_with_toolchain(*args):
        if args[:2] == ("buildx", "build"):
            staged.append((Path(args[-1]) / "sdk-wheel" / wheel.name).read_bytes())
        if args[0] == "run":
            return b"1.0.0 1\n"
        return original_docker(*args)

    monkeypatch.setattr(app_project, "docker", docker_with_toolchain)
    with pytest.raises(ValueError, match="SDK wheel changed"):
        app_project.build_project(root, bundle)
    document["toolchain"]["sdk"]["sha256"] = hashlib.sha256(wheel.read_bytes()).hexdigest()
    (root / "app.json").write_text(json.dumps(document))
    assert app_project.build_project(root, bundle)["release"] == document["release"]
    assert staged == [b"sdk bytes"]


def test_live_settings_build_refuses_bundle_without_settings_runtime(project):
    root, bundle, _calls, _state = project
    document = read_json(root / "app.json")
    document["runtime_settings"] = {"schema": {}, "defaults": {}}
    with pytest.raises(ValueError, match="does not include ringfour_runtime.settings"):
        app_project.verify_bundle(bundle, document)


@pytest.mark.parametrize(
    "raw",
    [
        '{"x":1,"x":2}',
        '{"x":NaN}',
        '{"x":"\\ud800"}',
        '{"x":1e400}',
        '{"x":1e-400}',
        '{"x":' + "9" * 400 + "}",
    ],
)
def test_raw_reader_rejects_duplicate_unicode_and_unsafe_numbers(tmp_path, raw):
    path = tmp_path / "invalid.json"
    path.write_text(raw)
    with pytest.raises(ValueError):
        read_json(path)


def test_bounded_float_manifest_and_canonical_digest_fixture(tmp_path):
    from jsonschema import Draft202012Validator, ValidationError

    fixture = json.loads(
        (CONTRACTS / "fixtures/apps/v2/floating-point.json").read_text()
    )
    manifest = validate_manifest(fixture["manifest"])
    assert "sha256:" + fingerprint(manifest) == fixture["canonical_digest"]
    configuration = tmp_path / "configuration.json"
    configuration.write_text(json.dumps(fixture["configuration_values"]))
    values = read_json(configuration)
    schema = copy.deepcopy(manifest["configuration"]["schema"])
    for rule in schema["properties"].values():
        if rule["type"] == "number":
            rule.setdefault("minimum", -9007199254740991)
            rule.setdefault("maximum", 9007199254740991)
    Draft202012Validator(schema).validate(values)
    assert values == fixture["configuration_values"]

    for raw in ('{"ratio":1e400}', '{"ratio":1e-400}'):
        configuration.write_text(raw)
        with pytest.raises(ValueError):
            read_json(configuration)
    for invalid in (
        {**values, "ratio": True},
        {**values, "ratio": "0.2"},
        {**values, "ratio": 1.000001},
        {**values, "ratio": 9007199254740992},
    ):
        with pytest.raises(ValidationError):
            Draft202012Validator(schema).validate(invalid)


@pytest.mark.parametrize(
    "name",
    [
        "../main.py",
        "/etc/passwd",
        ".env",
        "ringfour.context.json",
        ".git/config",
        "credentials.json",
    ],
)
def test_unsafe_or_credential_inputs_fail_before_build(project, name):
    root, _bundle, calls, _ = project
    doc = read_json(root / "app.json")
    doc["build"]["files"] = [name]
    with pytest.raises(ValueError):
        validate_authoring(doc)
    assert not calls


def test_build_context_omits_undeclared_secrets_and_checks_wheel_hashes(project):
    root, bundle, calls, _ = project
    built = app_project.build_project(root, bundle)
    assert built["targets"][0]["image_id"].startswith("sha256:")
    (bundle / "linux-amd64/wheels/test.whl").write_text("tampered")
    before = len(calls)
    with pytest.raises(ValueError, match="checksum"):
        app_project.build_project(root, bundle)
    assert len(calls) == before


def test_symlink_input_and_changed_source_refuse_publication(project):
    root, bundle, calls, _ = project
    app_project.build_project(root, bundle)
    (root / "main.py").write_text("changed")
    with pytest.raises(ValueError, match="changed"):
        app_project.prepare_publication(root)
    assert not any(call[:2] == ("image", "push") for call in calls)
    (root / "main.py").unlink()
    (root / "main.py").symlink_to(root / "Dockerfile")
    with pytest.raises(ValueError, match="symlink"):
        app_project.build_project(root, bundle)


def test_frozen_release_reuses_digest_without_repush_and_new_version_preserves_history(
    project,
):
    root, bundle, calls, _ = project
    app_project.build_project(root, bundle)
    manifest = app_project.prepare_publication(root)
    frozen = (root / ".ringfour/releases/0.1.0.json").read_bytes()
    before = sum(call[:2] == ("image", "push") for call in calls)
    assert app_project.prepare_publication(root) == manifest
    assert sum(call[:2] == ("image", "push") for call in calls) == before
    with pytest.raises(ValueError, match="frozen"):
        app_project.build_project(root, bundle)
    doc = read_json(root / "app.json")
    doc["release"] = "0.2.0"
    (root / "app.json").write_text(json.dumps(doc))
    (root / "main.py").write_text("new version")
    app_project.build_project(root, bundle)
    updated = app_project.prepare_publication(root)
    assert updated["release"] == "0.2.0" and manifest["release"] == "0.1.0"
    assert (root / ".ringfour/releases/0.1.0.json").read_bytes() == frozen


def test_replaced_local_image_index_and_wrong_digest_are_refused(project):
    root, bundle, _calls, state = project
    app_project.build_project(root, bundle)
    state["id"] = "sha256:" + "2" * 64
    with pytest.raises(ValueError, match="replaced"):
        app_project.prepare_publication(root)
    state["id"] = "sha256:" + "1" * 64
    state["type"] = "application/vnd.oci.image.index.v1+json"
    with pytest.raises(ValueError, match="concrete"):
        app_project.prepare_publication(root)
    state.pop("type")
    target = {"os": "linux", "architecture": "amd64"}
    with pytest.raises(ValueError, match="digest"):
        app_project.inspect_remote(
            "registry.example.com/team/check@sha256:" + "0" * 64, target
        )


def test_cli_publication_retry_uses_same_request_key_and_retires_legacy_group(
    project, monkeypatch, tmp_path
):
    root, bundle, _calls, _state = project
    app_project.build_project(root, bundle)
    settings = Settings(
        "http://api.example.invalid",
        "auth.example.invalid",
        "client",
        "https://api.ringfour.eu",
        tmp_path / "config.json",
        {"selected_org": ORG},
    )
    monkeypatch.setattr(main, "load_settings", lambda: settings)

    class Store:
        def load(self, _):
            return {"access_token": "never-in-build"}

    monkeypatch.setattr(main.auth, "TokenStore", Store)
    requests = []

    def request(method, path, *_args, **kwargs):
        requests.append((method, path, kwargs))
        return httpx.Response(
            201, json={"id": "revision", "manifest": kwargs["json"]["manifest"]}
        )

    monkeypatch.setattr(main, "_api_request", request)
    for _ in range(2):
        result = CliRunner().invoke(main.app, ["app", "publish", str(root), "--json"])
        assert result.exit_code == 0, result.output
        assert json.loads(result.stdout)["id"] == "revision"
    assert requests[0][2]["headers"] == requests[1][2]["headers"]
    assert requests[0][2]["json"] == requests[1][2]["json"]
    assert CliRunner().invoke(main.app, ["mission", "--help"]).exit_code == 2


def test_template_initialization_checks_hash_and_refuses_present_destination(
    tmp_path, monkeypatch
):
    doc = json.loads((CONTRACTS / "fixtures/apps/v1/manifest.json").read_text())
    for target in doc["targets"]:
        target["repository"] = "registry.example.com/team/check"
        target.pop("image")
    doc["build"] = {"dockerfile": "Dockerfile", "files": ["main.py"]}
    doc["wheel_bundle"] = {"version": "test", "sha256": "a" * 64}
    payload = io.BytesIO()
    with tarfile.open(fileobj=payload, mode="w") as archive:
        for name, text in [
            ("app.json", json.dumps(doc)),
            ("main.py", "fixture"),
            ("Dockerfile", "FROM scratch"),
        ]:
            data = text.encode()
            member = tarfile.TarInfo("robot-check/" + name)
            member.size = len(data)
            archive.addfile(member, io.BytesIO(data))
    archive = tmp_path / "template.tar.gz"
    archive.write_bytes(gzip.compress(payload.getvalue(), mtime=0))
    resources = tmp_path / "resources/data"
    resources.mkdir(parents=True)
    (resources / "robot-check-template.json").write_text(
        json.dumps(
            {
                "version": "test",
                "archive_name": archive.name,
                "sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
            }
        )
    )
    monkeypatch.setattr(app_project, "files", lambda _name: resources.parent)
    destination = tmp_path / "new"
    app_project.init_project(
        destination, "my-app", "registry.example.com/team/mine", archive
    )
    assert read_json(destination / "app.json")["app_key"] == "my-app"
    with pytest.raises(ValueError, match="exist"):
        app_project.init_project(
            destination, "x", "registry.example.com/team/x", archive
        )
    archive.write_bytes(b"changed")
    with pytest.raises(ValueError, match="checksum"):
        app_project.init_project(
            tmp_path / "other", "x", "registry.example.com/team/x", archive
        )


def test_canonical_unicode_fixture_matches_api_and_daemon():
    fixture = json.loads(
        (CONTRACTS / "fixtures/apps/v1/canonical-unicode.json").read_text()
    )
    assert (
        "sha256:" + fingerprint(validate_manifest(fixture["manifest"]))
        == fixture["digest"]
    )
