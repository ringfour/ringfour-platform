import asyncio
from contextlib import asynccontextmanager

import httpx
from fastapi import FastAPI

from . import __version__
from .config import Settings
from .connection import ConnectionManager
from .db import migrate
from .enrollment import check_device_state
from .enrollment import router as enrollment_router
from .health import router as health_router


@asynccontextmanager
async def lifespan(app: FastAPI):
    settings: Settings = app.state.settings
    connection = ConnectionManager(settings)
    app.state.connection_manager = connection
    task = asyncio.create_task(connection.run())
    try:
        yield
    finally:
        connection.stop()
        task.cancel()
        try:
            await task
        except asyncio.CancelledError:
            pass


def create_app(settings: Settings | None = None, http_client_factory=None) -> FastAPI:
    settings = settings or Settings.from_env()
    migrate(settings.db_path)
    check_device_state(settings.db_path, settings.credential_path)
    app = FastAPI(title="ringfour-daemon", version=__version__, lifespan=lifespan)
    app.state.settings = settings
    app.state.http_client_factory = http_client_factory or (lambda: httpx.Client(timeout=2.0))
    app.include_router(health_router)
    app.include_router(enrollment_router)
    return app
