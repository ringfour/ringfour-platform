"""Test-only daemon entrypoint: suspend at the committed-before-ack boundary."""
import asyncio
from pathlib import Path

from ringfour_daemon.connection import ConnectionManager
from ringfour_daemon.main import main

original_queue = ConnectionManager._queue


async def pause_ack(self, outbound, state, kind, payload):
    if kind == "ack":
        Path("/data/pre-ack-operation").write_text(payload["operation_id"])
        # The test partitions and kills this process while it is suspended.
        await asyncio.Event().wait()
    await original_queue(self, outbound, state, kind, payload)


ConnectionManager._queue = pause_ack
main()
