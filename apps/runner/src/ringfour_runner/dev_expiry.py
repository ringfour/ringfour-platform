"""Stop and clean expired development work even if Platform is unreachable."""
import asyncio
import logging
import shutil
import sqlite3
from datetime import UTC, datetime
from pathlib import Path

from .dev_execution import stop_session_execution
from .executions import connection

logger = logging.getLogger(__name__)


async def expire_one(app, session_id):
    settings = app.state.settings
    async with app.state.dev_session_lock:
        with connection(settings) as conn:
            row = conn.execute("SELECT state,expires_at FROM dev_sessions WHERE session_id=?", (session_id,)).fetchone()
            if not row or row["state"] == "closed" or datetime.fromisoformat(row["expires_at"]) > datetime.now(UTC):
                return
            conn.execute("UPDATE dev_sessions SET state='closing' WHERE session_id=?", (session_id,))
        if not await stop_session_execution(app, session_id):
            return
        root = Path(settings.data_dir) / "dev-sessions"
        workspace = root / session_id
        if root.is_symlink() or workspace.is_symlink():
            raise OSError("Unsafe session path")
        if workspace.exists():
            shutil.rmtree(workspace)
        with connection(settings) as conn:
            conn.execute("UPDATE dev_sessions SET state='closed' WHERE session_id=?", (session_id,))


async def expire_sessions(app):
    while True:
        try:
            with connection(app.state.settings) as conn:
                rows = conn.execute("SELECT session_id,expires_at FROM dev_sessions WHERE state != 'closed'").fetchall()
            now = datetime.now(UTC)
            for row in rows:
                if datetime.fromisoformat(row["expires_at"]) <= now:
                    await expire_one(app, row["session_id"])
        except (OSError, ValueError, sqlite3.Error):
            logger.warning("Development session expiry failed; retrying.")
        await asyncio.sleep(1)
