import shutil
import subprocess
from pathlib import Path

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse

from . import __version__

router = APIRouter()


@router.get("/health/live")
def live() -> dict[str, str]:
    return {"status": "ok", "version": __version__}


@router.get("/health/ready")
def ready(request: Request) -> JSONResponse:
    checks = {
        "volume": _check_volume(request.app.state.settings.data_dir),
        "uv": _check_uv(),
        "auth": "ok" if request.app.state.settings.auth_token else "fail",
    }
    status = "ok" if all(v == "ok" for v in checks.values()) else "fail"
    return JSONResponse(
        {"status": status, "version": __version__, "checks": checks},
        status_code=200 if status == "ok" else 503,
    )


def _check_volume(data_dir: str) -> str:
    probe = Path(data_dir) / ".readiness-probe"
    try:
        probe.write_text("ok")
        probe.unlink()
        return "ok"
    except OSError:
        return "fail"


def _check_uv() -> str:
    if shutil.which("uv") is None:
        return "fail"
    try:
        result = subprocess.run(
            ["uv", "--version"], capture_output=True, timeout=5, check=False
        )
        return "ok" if result.returncode == 0 else "fail"
    except (OSError, subprocess.TimeoutExpired):
        return "fail"
