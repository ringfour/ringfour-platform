import io
import zipfile

import pytest

from ringfour_cli.script_bundle import build_script_bundle


def test_bundle_is_deterministic_and_contains_the_lock(tmp_path):
    script = tmp_path / "hello.py"
    script.write_text("print('hello')\n")
    first = build_script_bundle(script)
    second = build_script_bundle(script)
    assert first == second
    with zipfile.ZipFile(io.BytesIO(first.archive)) as archive:
        assert sorted(archive.namelist()) == ["hello.py", "requirements.lock"]
        assert archive.read("hello.py") == b"print('hello')\n"
        assert archive.read("requirements.lock") == b""


def test_bundle_rejects_symlinks_invalid_names_and_oversized_files(tmp_path):
    script = tmp_path / "hello.py"
    script.write_text("print('ok')")
    link = tmp_path / "link.py"
    link.symlink_to(script)
    with pytest.raises(ValueError, match="symlink"):
        build_script_bundle(link)
    invalid = tmp_path / "héllo.py"
    invalid.write_text("pass")
    with pytest.raises(ValueError, match="ASCII"):
        build_script_bundle(invalid)
    script.write_bytes(b"x" * 262145)
    with pytest.raises(ValueError, match="256 KiB"):
        build_script_bundle(script)
