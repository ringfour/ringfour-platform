from fastapi import FastAPI

from . import __version__
from .config import Settings
from .health import router as health_router


def create_app(settings: Settings | None = None) -> FastAPI:
    settings = settings or Settings.from_env()
    app = FastAPI(title="ringfour-runner", version=__version__)
    app.state.settings = settings
    app.include_router(health_router)
    return app
