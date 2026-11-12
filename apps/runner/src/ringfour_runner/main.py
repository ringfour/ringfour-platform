import asyncio
import os
import signal
from contextlib import asynccontextmanager

from fastapi import FastAPI

from . import __version__
from .config import Settings
from .executions import initialize
from .executions import router as executions_router
from .health import router as health_router
from .preparation import router as preparation_router


@asynccontextmanager
async def lifespan(app: FastAPI):
    initialize(app.state.settings)
    app.state.tasks = set()
    app.state.processes = {}
    app.state.prepare_lock = asyncio.Lock()
    yield
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
    return app
