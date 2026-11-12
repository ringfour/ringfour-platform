import os
import sys
from pathlib import Path

print("hello from uploaded script", flush=True)
print("script stderr", file=sys.stderr, flush=True)
Path(os.environ["RINGFOUR_ARTIFACT_DIR"], "result.txt").write_bytes(b"ringfour artifact\n")
