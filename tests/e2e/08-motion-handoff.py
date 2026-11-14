"""Exercise the real local handoff across processes without ROS or robot motion."""
import asyncio
import json
import sqlite3
import subprocess
import sys
import tempfile
import time
from pathlib import Path


async def agent(socket_path, state_dir, seed):
    from ringfour_agent.bootstrap import build_agent
    from ringfour_agent.profile import load_profile

    profile = Path(__file__).resolve().parents[3] / 'ringfour-testing/platform_tests/fixtures/stub.yaml'
    server, ctx = build_agent(load_profile(profile), unix_path=str(Path(socket_path).parent / 'robot.sock'),
                              state_dir=state_dir, audit_dir=str(Path(state_dir) / 'audit'),
                              motion_handoff_path=socket_path)
    if seed:
        ctx.motion_journal.append('motion.accepted', operation_id='fixture-task')
    await server.start()
    print('ready', flush=True)
    try:
        await asyncio.Event().wait()
    finally:
        await server.close()
        ctx.audit.close()


async def daemon_copy(db_path, socket_path, acknowledge):
    from ringfour_daemon import agent_motion
    from ringfour_daemon.config import Settings
    from ringfour_daemon.db import migrate

    migrate(db_path)
    if acknowledge:
        from ringfour_daemon.main import create_app

        settings = Settings(db_path=db_path, runner_url='http://127.0.0.1:1',
                            credential_path=db_path + '.credential', agent_motion_socket=socket_path)
        app = create_app(settings)
        async with app.router.lifespan_context(app):
            for _ in range(200):
                stream, sequence = agent_motion._checkpoint(db_path)
                if stream is not None:
                    page = await agent_motion._request(socket_path, {
                        'method': 'read', 'stream_id': stream, 'after': sequence, 'limit': 100,
                    })
                    if page['acknowledged_sequence'] == sequence:
                        break
                await asyncio.sleep(.025)
            else:
                raise RuntimeError('Daemon did not acknowledge the saved checkpoint')
    else:
        stream, after = agent_motion._checkpoint(db_path)
        page = await agent_motion._request(socket_path, {
            'method': 'read', 'stream_id': stream, 'after': after, 'limit': 100,
        })
        agent_motion._store(db_path, stream, after, page)
    print('copied')


def run():
    here = Path(__file__).resolve()
    agent_python = here.parents[3] / 'ringfour-testing/.venv/bin/python'
    with tempfile.TemporaryDirectory(dir='/tmp', prefix='r4-e2e-') as directory:
        socket_path = str(Path(directory) / 'motion.sock')
        state_dir = str(Path(directory) / 'state')
        db_path = str(Path(directory) / 'daemon.db')

        def start(seed):
            proc = subprocess.Popen([str(agent_python), str(here), 'agent', socket_path, state_dir, str(int(seed))],
                                    stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            if proc.stdout.readline().strip() != 'ready':
                raise RuntimeError(f'Agent handoff failed: {proc.stderr.read()}')
            return proc

        def copy(acknowledge):
            subprocess.run([sys.executable, str(here), 'daemon', db_path, socket_path, str(int(acknowledge))],
                           check=True, capture_output=True, text=True)

        def stop(proc):
            proc.terminate()
            proc.wait(timeout=5)

        proc = start(True)
        journal_path = next(Path(state_dir).glob('motion-*.sqlite3'))
        try:
            copy(False)  # Crash boundary: committed in daemon, not acknowledged to Agent.
        finally:
            stop(proc)
        with sqlite3.connect(journal_path) as conn:
            assert conn.execute('SELECT acknowledged FROM motion_stream').fetchone()[0] == 0
        proc = start(False)
        try:
            for _ in range(100):
                with sqlite3.connect(journal_path) as conn:
                    if conn.execute('SELECT last_sequence FROM motion_stream').fetchone()[0] == 2:
                        break
                time.sleep(.05)
            else:
                raise AssertionError('Unclean restart did not record the history gap')
            copy(True)
            copy(True)  # A lost acknowledgement response may cause the same checkpoint to be sent again.
        finally:
            stop(proc)
        with sqlite3.connect(journal_path) as conn:
            assert conn.execute('SELECT acknowledged FROM motion_stream').fetchone()[0] == 2
            assert conn.execute('SELECT count(*) FROM motion_events').fetchone()[0] == 0
        with sqlite3.connect(db_path) as conn:
            rows = conn.execute('SELECT event_json FROM agent_motion_inbox').fetchall()
            assert len(rows) == 2 and json.loads(rows[0][0])['operation_id'] == 'fixture-task'
            assert json.loads(rows[1][0])['event_type'] == 'motion.history_incomplete'
        print('Agent journal to daemon inbox: restart and lost acknowledgement passed')


if __name__ == '__main__':
    if len(sys.argv) == 1:
        run()
    elif sys.argv[1] == 'agent':
        asyncio.run(agent(sys.argv[2], sys.argv[3], bool(int(sys.argv[4]))))
    else:
        asyncio.run(daemon_copy(sys.argv[2], sys.argv[3], bool(int(sys.argv[4]))))
