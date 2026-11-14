import asyncio
import os
import socket
import threading
from contextlib import asynccontextmanager

import httpx
from fastapi import FastAPI

from . import __version__
from .agent_motion import run as run_agent_motion
from .config import Settings
from .connection import ConnectionManager
from .db import migrate
from .enrollment import check_device_state
from .enrollment import router as enrollment_router
from .health import router as health_router
from .local_advertisement import publish_local_endpoint
from .local_control import create_local_app
from .local_execution import reconcile as reconcile_local_execution
from .local_identity import local_identity
from .local_source import reconcile as reconcile_local_source
from .logbook import deliver as deliver_logbook
from .pairing import run_pairing
from .runtime_logbook import router as runtime_logbook_router
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
    source_task = asyncio.create_task(reconcile_local_source(settings))
    execution_task = asyncio.create_task(reconcile_local_execution(settings))
    advertisement_task = asyncio.create_task(publish_local_endpoint(settings))
    logbook_task = asyncio.create_task(deliver_logbook(settings))
    agent_motion_task = asyncio.create_task(run_agent_motion(settings))
    try:
        yield
    finally:
        connection.stop()
        task.cancel()
        pairing_task.cancel()
        sync_task.cancel()
        source_task.cancel()
        execution_task.cancel()
        advertisement_task.cancel()
        logbook_task.cancel()
        agent_motion_task.cancel()
        await asyncio.gather(pairing_task, sync_task, source_task, execution_task, advertisement_task, logbook_task, agent_motion_task, return_exceptions=True)
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
    app.include_router(runtime_logbook_router)
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
    if settings.local_dev_enabled:
        identity_path = settings.local_tls_path
        local_identity(identity_path)
        local_socket = socket.create_server((
            settings.local_dev_host,
            settings.local_dev_port,
        ))
        local_server = uvicorn.Server(uvicorn.Config(
            create_local_app(settings), ssl_certfile=identity_path, ssl_keyfile=identity_path, access_log=False,
        ))
        threading.Thread(target=local_server.run, kwargs={"sockets": [local_socket]}, daemon=True).start()
    uvicorn.run(
        control_app,
        host=os.environ.get("RINGFOUR_DAEMON_HOST", "0.0.0.0"),
        port=int(os.environ.get("RINGFOUR_DAEMON_PORT", "8081")),
    )
