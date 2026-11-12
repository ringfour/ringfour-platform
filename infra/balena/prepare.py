"""Prepare a standalone Balena build directory without copying app venvs."""
import shutil
import sys
from pathlib import Path


def main():
    if len(sys.argv) != 2:
        raise SystemExit("Usage: python3 infra/balena/prepare.py /absolute/new/build-directory")
    destination = Path(sys.argv[1])
    if not destination.is_absolute() or destination.exists():
        raise SystemExit("Destination must be an absolute path that does not already exist.")
    root = Path(__file__).resolve().parents[2]
    destination.mkdir(parents=True)
    shutil.copy2(root / "infra/balena/docker-compose.yml", destination / "docker-compose.yml")
    for name in ("daemon", "runner"):
        shutil.copytree(root / "apps" / name, destination / name,
                        ignore=shutil.ignore_patterns(".venv", "__pycache__", ".pytest_cache", ".ruff_cache", "tests", "*.pyc"))
    print(f"Prepared Balena build directory: {destination}")


if __name__ == "__main__":
    main()
