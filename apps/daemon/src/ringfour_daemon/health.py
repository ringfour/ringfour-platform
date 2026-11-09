import sqlite3

import httpx
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
        "sqlite": _check_sqlite(request.app.state.settings.db_path),
        "runner": _check_runner(request),
    }
    status = "ok" if all(v == "ok" for v in checks.values()) else "fail"
    return JSONResponse(
        {"status": status, "version": __version__, "checks": checks},
        status_code=200 if status == "ok" else 503,
    )


def _check_sqlite(db_path: str) -> str:
    try:
        with sqlite3.connect(db_path, timeout=2) as conn:
            conn.execute("SELECT 1")
            conn.execute("BEGIN IMMEDIATE")
            conn.rollback()
        return "ok"
    except sqlite3.Error:
        return "fail"


def _check_runner(request: Request) -> str:
    try:
        with request.app.state.http_client_factory() as client:
            response = client.get(f"{request.app.state.settings.runner_url}/health/ready")
            body = response.json()
            return "ok" if response.status_code == 200 and body.get("status") == "ok" else "fail"
    except (httpx.HTTPError, ValueError):
        return "fail"
