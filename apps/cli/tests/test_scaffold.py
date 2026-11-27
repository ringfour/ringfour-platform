import subprocess

from typer.testing import CliRunner

from ringfour_cli.main import app


def test_scaffold_clones_from_git_and_preserves_existing_work(tmp_path, monkeypatch):
    source = tmp_path / "template"
    subprocess.run(["git", "init", "-q", str(source)], check=True)
    (source / "main.py").write_text("print('template')\n")
    subprocess.run(["git", "add", "main.py"], cwd=source, check=True)
    subprocess.run(
        [
            "git",
            "-c",
            "user.name=Test",
            "-c",
            "user.email=test@example.org",
            "-c",
            "commit.gpgsign=false",
            "commit",
            "-qm",
            "Starter",
        ],
        cwd=source,
        check=True,
    )
    monkeypatch.chdir(tmp_path)
    monkeypatch.setenv("RINGFOUR_TEMPLATE_REPO", str(source))
    runner = CliRunner()

    assert runner.invoke(app, ["scaffold"]).exit_code == 0
    project = tmp_path / "my-ringfour-app"
    assert (project / "main.py").read_bytes() == (source / "main.py").read_bytes()
    assert subprocess.check_output(
        ["git", "remote", "get-url", "origin"], cwd=project, text=True
    ).strip() == str(source)

    (project / "main.py").write_text("my changes\n")
    assert runner.invoke(app, ["scaffold"]).exit_code != 0
    assert (project / "main.py").read_text() == "my changes\n"
