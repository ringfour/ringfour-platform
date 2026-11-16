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
    (root / "ringfour.context.json").write_text("local only")
    first = build_mission_bundle(root)
    second = build_mission_bundle(root)
    assert second.bundle.archive_sha256 == first.bundle.archive_sha256
    assert second.manifest == first.manifest
    second.bundle.close()
    assert first.manifest["source_digest"] == first.bundle.revision_digest
    assert first.manifest["lock_file"] == "requirements.lock"
    with zipfile.ZipFile(first.bundle.archive) as archive:
        assert sorted(archive.namelist()) == ["app/helper.py", "app/main.py", "requirements.lock"]
        assert archive.read("app/main.py") == b"print('hello')\n"
    (root / "app" / "helper.py").write_text("answer = 43\n")
    changed = build_mission_bundle(root)
    assert changed.manifest["source_digest"] != first.manifest["source_digest"]
    changed.bundle.close()
    first.bundle.close()


@pytest.mark.parametrize("entrypoint", ["../outside.py", "/absolute.py", "app\\main.py", "app/.hidden.py"])
def test_manifest_rejects_unsafe_or_missing_entrypoint(tmp_path, entrypoint):
    root = project(tmp_path)
    path = root / "mission.toml"
    path.write_text(path.read_text().replace("app/main.py", entrypoint))
    with pytest.raises(ValueError):
        build_mission_bundle(root)


def test_rejects_symlinks_and_accepts_large_sources(tmp_path):
    root = project(tmp_path)
    (root / "link.py").symlink_to(root / "app" / "main.py")
    with pytest.raises(ValueError, match="symlink"):
        build_mission_bundle(root)
    (root / "link.py").unlink()
    (root / "large.py").write_bytes(b"x" * 262145)
    large = build_mission_bundle(root)
    try:
        with zipfile.ZipFile(large.bundle.archive) as archive:
            assert archive.getinfo("large.py").file_size == 262145
    finally:
        large.bundle.close()


def test_empty_project_reports_missing_manifest(tmp_path):
    with pytest.raises(ValueError, match="mission.toml"):
        build_mission_bundle(tmp_path)


def test_file_backed_archive_stream_replays_without_buffering(tmp_path):
    from ringfour_cli.main import _MissionArchiveStream

    bundle = build_mission_bundle(project(tmp_path)).bundle
    try:
        stream = _MissionArchiveStream(bundle.archive)
        first = b"".join(stream)
        second = b"".join(stream)
        assert len(first) == bundle.archive_size
        assert first == second
        assert max(map(len, _MissionArchiveStream(bundle.archive))) <= 65536
    finally:
        bundle.close()
