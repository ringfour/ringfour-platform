import io
import zipfile

import pytest

from ringfour_cli.mission_bundle import build_mission_bundle


def project(tmp_path):
    (tmp_path / "mission.toml").write_text('schema_version = 1\nname = "hello"\nentrypoint = "app/main.py"\npython_requirement = ">=3.13,<3.14"\nlock_file = "requirements.lock"\n')
    (tmp_path / "app").mkdir()
    (tmp_path / "app" / "main.py").write_text("print('hello')\n")
    (tmp_path / "app" / "helper.py").write_text("answer = 42\n")
    (tmp_path / "requirements.lock").write_bytes(b"")
    return tmp_path


def test_mission_bundle_is_deterministic_and_excludes_manifest_and_hidden_files(tmp_path):
    root = project(tmp_path)
    (root / ".secret").write_text("not uploaded")
    first = build_mission_bundle(root)
    assert build_mission_bundle(root) == first
    assert first.manifest["source_digest"] == first.bundle.revision_digest
    assert first.manifest["lock_file"] == "requirements.lock"
    with zipfile.ZipFile(io.BytesIO(first.bundle.archive)) as archive:
        assert sorted(archive.namelist()) == ["app/helper.py", "app/main.py", "requirements.lock"]
        assert archive.read("app/main.py") == b"print('hello')\n"
    (root / "app" / "helper.py").write_text("answer = 43\n")
    assert build_mission_bundle(root).manifest["source_digest"] != first.manifest["source_digest"]


@pytest.mark.parametrize("entrypoint", ["../outside.py", "/absolute.py", "app\\main.py", "app/.hidden.py"])
def test_manifest_rejects_unsafe_or_missing_entrypoint(tmp_path, entrypoint):
    root = project(tmp_path)
    path = root / "mission.toml"
    path.write_text(path.read_text().replace("app/main.py", entrypoint))
    with pytest.raises(ValueError):
        build_mission_bundle(root)


def test_rejects_symlinks_and_oversized_sources(tmp_path):
    root = project(tmp_path)
    (root / "link.py").symlink_to(root / "app" / "main.py")
    with pytest.raises(ValueError, match="symlink"):
        build_mission_bundle(root)
    (root / "link.py").unlink()
    (root / "large.py").write_bytes(b"x" * 262145)
    with pytest.raises(ValueError, match="byte limit"):
        build_mission_bundle(root)


def test_empty_project_reports_missing_manifest(tmp_path):
    with pytest.raises(ValueError, match="mission.toml"):
        build_mission_bundle(tmp_path)
