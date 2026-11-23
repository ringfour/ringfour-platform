#!/usr/bin/env python3
"""Produce the pinned standalone template archive; consumers need only the archive."""

import argparse
import gzip
import hashlib
import json
import os
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-repo", type=Path, default=ROOT.parent / "robot-check")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    pointer = json.loads(
        (ROOT / "apps/cli/src/ringfour_cli/data/robot-check-template.json").read_text()
    )
    output = (
        args.output
        or Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share"))
        / "ringfour/app-templates/robot-check"
        / pointer["archive_name"]
    )
    source = subprocess.check_output(
        [
            "git",
            "-C",
            str(args.source_repo),
            "archive",
            "--format=tar",
            "--prefix=robot-check/",
            pointer["source_commit"],
        ]
    )
    archive = gzip.compress(source, compresslevel=9, mtime=0)
    digest = hashlib.sha256(archive).hexdigest()
    if digest != pointer["sha256"]:
        parser.exit(
            1,
            "Template archive does not match the pinned release; do not overwrite its checksum.\n",
        )
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists():
        if hashlib.sha256(output.read_bytes()).hexdigest() != digest:
            parser.exit(1, "Output already exists with different bytes.\n")
    else:
        with output.open("xb") as stream:
            stream.write(archive)
    print(
        json.dumps(
            {
                "archive": str(output),
                "source_commit": pointer["source_commit"],
                "sha256": digest,
            }
        )
    )


if __name__ == "__main__":
    main()
