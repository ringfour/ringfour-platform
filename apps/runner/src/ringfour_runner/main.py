import asyncio
import os
import signal
from contextlib import asynccontextmanager

from fastapi import FastAPI

from . import __version__
from .config import Settings
from .dev_execution import initialize as initialize_dev_execution
from .dev_execution import router as dev_execution_router
from .dev_expiry import expire_sessions
from .dev_sessions import initialize as initialize_dev_sessions
from .dev_sessions import router as dev_sessions_router
from .dev_source import initialize as initialize_dev_source
from .dev_source import router as dev_source_router
from .executions import initialize
from .executions import router as executions_router
from .health import router as health_router
from .preparation import router as preparation_router


@asynccontextmanager
async def lifespan(app: FastAPI):
    initialize(app.state.settings)
    initialize_dev_sessions(app.state.settings)
    initialize_dev_source(app.state.settings)
    initialize_dev_execution(app.state.settings)
    app.state.dev_session_lock = asyncio.Lock()
    app.state.tasks = set()
    app.state.processes = {}
    app.state.prepare_lock = asyncio.Lock()
    expiry_task = asyncio.create_task(expire_sessions(app))
    yield
    expiry_task.cancel()
    await asyncio.gather(expiry_task, return_exceptions=True)
    for proc in list(app.state.processes.values()):
        if proc.returncode is None:
            try:
                os.killpg(proc.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
    for task in list(app.state.tasks):
        task.cancel()
    await asyncio.gather(*app.state.tasks, return_exceptions=True)


def create_app(settings: Settings | None = None) -> FastAPI:
    settings = settings or Settings.from_env()
    app = FastAPI(title="ringfour-runner", version=__version__, lifespan=lifespan)
    app.state.settings = settings
    app.include_router(health_router)
    app.include_router(executions_router)
    app.include_router(preparation_router)
    app.include_router(dev_sessions_router)
    app.include_router(dev_source_router)
    app.include_router(dev_execution_router)
    return app
