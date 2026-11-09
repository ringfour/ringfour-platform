import httpx
from fastapi import FastAPI

from . import __version__
from .config import Settings
from .db import migrate
from .health import router as health_router


def create_app(settings: Settings | None = None, http_client_factory=None) -> FastAPI:
    settings = settings or Settings.from_env()
    migrate(settings.db_path)
    app = FastAPI(title="ringfour-daemon", version=__version__)
    app.state.settings = settings
    app.state.http_client_factory = http_client_factory or (lambda: httpx.Client(timeout=2.0))
    app.include_router(health_router)
    return app
