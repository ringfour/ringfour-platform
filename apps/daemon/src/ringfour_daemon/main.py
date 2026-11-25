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
from .enrollment import check_device_state, load_identity
from .enrollment import router as enrollment_router
from .health import router as health_router
from .logbook import deliver as deliver_logbook
from .pairing import run_pairing
from .runtime_artifacts import router as runtime_artifacts_router
from .runtime_interactions import deliver as deliver_interactions
from .runtime_interactions import reconcile as reconcile_interactions
from .runtime_interactions import router as runtime_interactions_router
from .runtime_logbook import router as runtime_logbook_router
from .runtime_settings import router as runtime_settings_router
from .status_page import create_status_app


@asynccontextmanager
async def lifespan(app: FastAPI):
    settings: Settings = app.state.settings
    from .app_engine import Executor, run_reconciliation
    executor = None
    # The lifetime writer fence precedes any App consumer. Enrollment may be pending.
    if settings.apps_enabled and load_identity(settings.db_path):
        executor = Executor(settings).open()
    app.state.app_executor = executor
    connection = ConnectionManager(settings)
    connection.app_executor = executor
    app.state.connection_manager = connection
    task = asyncio.create_task(connection.run())
    app_engine_task = asyncio.create_task(run_reconciliation(settings, connection))
    from .app_runs import run as run_apps
    app_run_task = asyncio.create_task(run_apps(settings, connection))
    pairing_task = asyncio.create_task(run_pairing(settings))
    logbook_task = asyncio.create_task(deliver_logbook(settings))
    from .app_runtime import deliver as deliver_app_runtime
    app_runtime_task = asyncio.create_task(deliver_app_runtime(settings))
    agent_motion_task = asyncio.create_task(run_agent_motion(settings))
    interaction_task = asyncio.create_task(reconcile_interactions(settings))
    interaction_delivery_task = asyncio.create_task(deliver_interactions(settings))
    try:
        yield
    finally:
        connection.stop()
        task.cancel()
        app_engine_task.cancel()
        app_run_task.cancel()
        pairing_task.cancel()
        logbook_task.cancel()
        app_runtime_task.cancel()
        agent_motion_task.cancel()
        interaction_task.cancel()
        interaction_delivery_task.cancel()
        await asyncio.gather(app_runtime_task, app_run_task, app_engine_task, pairing_task, logbook_task, agent_motion_task, interaction_task, interaction_delivery_task, return_exceptions=True)
        try:
            await task
        except asyncio.CancelledError:
            pass
        if connection.app_executor:
            connection.app_executor.close()


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
    app.include_router(runtime_settings_router)
    app.include_router(runtime_interactions_router)
    app.include_router(runtime_artifacts_router)
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
