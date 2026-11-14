"""Start a real no-ROS Agent server with one synthetic motion journal event."""
import asyncio

from ringfour_agent.bootstrap import build_agent
from ringfour_agent.profile import load_profile
from ringfour_agent.server import Session


class FixtureSocket:
    async def send(self, _message):
        pass


async def main():
    server, ctx = build_agent(load_profile('/profiles/stub.yaml'), unix_path='/tmp/ringfour-test-agent.sock',
                              state_dir='/var/lib/ringfour/test-motion', audit_dir='/var/lib/ringfour/test-audit',
                              motion_handoff_path='/run/ringfour-motion/motion.sock')
    await server.start()
    async def measured(handle):
        handle.mark_dispatched()
        handle.record_stop({'verified': True, 'criterion': 'body-velocity-dwell', 'physical_stop': 'uncommissioned'})
        return {}
    task = ctx.tasks.start('mobility.navigate_to', Session(FixtureSocket()), measured)
    await ctx.tasks._tasks[task].aio_task
    print('synthetic motion task ready', flush=True)
    try:
        await asyncio.Event().wait()
    finally:
        await server.close()
        ctx.audit.close()


asyncio.run(main())
