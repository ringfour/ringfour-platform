import sqlite3

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse

from . import __version__
from .db import connect

router = APIRouter()


@router.get("/health/live")
def live() -> dict[str, str]:
    return {"status": "ok", "version": __version__}


@router.get("/health/ready")
def ready(request: Request) -> JSONResponse:
    checks = {
        "sqlite": _check_sqlite(request.app.state.settings.db_path),
        "engine": _check_engine(request),
    }
    status = "ok" if all(v == "ok" for v in checks.values()) else "fail"
    return JSONResponse(
        {"status": status, "version": __version__, "checks": checks},
        status_code=200 if status == "ok" else 503,
    )


def _check_sqlite(db_path: str) -> str:
    try:
        with connect(db_path, timeout=2) as conn:
            conn.execute("SELECT 1")
            conn.execute("BEGIN IMMEDIATE")
            conn.rollback()
        return "ok"
    except sqlite3.Error:
        return "fail"


def _check_engine(request):
    from .app_engine import Engine, EngineError
    engine = Engine(request.app.state.settings)
    try:
        engine.platform()
        return "ok"
    except EngineError:
        return "fail"
    finally:
        engine.close()
