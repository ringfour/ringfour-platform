import asyncio
import os
import socket
import threading
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
from .pairing import run_pairing
from .scripts import run_sync
from .status_page import create_status_app


@asynccontextmanager
async def lifespan(app: FastAPI):
    settings: Settings = app.state.settings
    connection = ConnectionManager(settings)
    app.state.connection_manager = connection
    task = asyncio.create_task(connection.run())
    pairing_task = asyncio.create_task(run_pairing(settings))
    sync_task = asyncio.create_task(run_sync(settings))
    try:
        yield
    finally:
        connection.stop()
        task.cancel()
        pairing_task.cancel()
        sync_task.cancel()
        await asyncio.gather(pairing_task, sync_task, return_exceptions=True)
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


def main() -> None:
    import uvicorn

    settings = Settings.from_env()
    control_app = create_app(settings)
    # The LAN listener serves only a read-only status app. Control endpoints
    # remain on the independent daemon listener.
    status_socket = socket.create_server((
        os.environ.get("RINGFOUR_STATUS_HOST", "0.0.0.0"),
        int(os.environ.get("RINGFOUR_STATUS_PORT", "8083")),
    ))
    status_server = uvicorn.Server(uvicorn.Config(create_status_app(settings, control_app), access_log=False))
    thread = threading.Thread(target=status_server.run, kwargs={"sockets": [status_socket]}, daemon=True)
    thread.start()
    uvicorn.run(
        control_app,
        host=os.environ.get("RINGFOUR_DAEMON_HOST", "0.0.0.0"),
        port=int(os.environ.get("RINGFOUR_DAEMON_PORT", "8081")),
    )
