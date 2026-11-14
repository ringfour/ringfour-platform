import runpy
import sys
from pathlib import Path
from types import ModuleType, SimpleNamespace

import pytest


def test_demo_records_decisions_and_closes_one_teleop_session(monkeypatch):
    entries, velocities, lifecycle = [], [], []

    class Teleop:
        def __enter__(self):
            lifecycle.append('open')
            return self

        def __exit__(self, *_):
            lifecycle.append('close')

        def drive(self, **values):
            velocities.append(values)

    class Robot:
        name = 'synthetic'
        capabilities = frozenset({'teleop', 'status'})

        def __init__(self, uri):
            assert uri == 'ws://fixture'

        def __enter__(self):
            return self

        def __exit__(self, *_):
            pass

        def teleop(self, *, watchdog):
            assert watchdog == .5
            return Teleop()

    sdk = ModuleType('ringfour')
    sdk.Robot = Robot
    runtime = ModuleType('ringfour_runtime')
    runtime.logbook = SimpleNamespace(info=lambda message, **fields: entries.append((message, fields)))
    monkeypatch.setitem(sys.modules, 'ringfour', sdk)
    monkeypatch.setitem(sys.modules, 'ringfour_runtime', runtime)
    monkeypatch.setenv('RINGFOUR_AGENT_URL', 'ws://fixture')
    monkeypatch.setattr('time.sleep', lambda _: None)
    runpy.run_path(str(Path(__file__).resolve().parents[2] / 'missions/logbook-demo/main.py'))
    assert lifecycle == ['open', 'close']
    assert velocities == [{'linear': .05, 'angular': 0.0}] * 10
    assert [message for message, _ in entries] == [
        'Logbook demonstration started', 'Robot capabilities discovered',
        'Starting wheel test', 'Wheel test finished',
    ]


def test_demo_reports_quarantine_without_hiding_original_error(monkeypatch, capsys):
    entries = []
    class SafetyViolation(Exception):
        def __init__(self, message):
            super().__init__(message)
            self.details = {'reason': 'quarantine', 'resources': {'restart': {'reason': 'unclean shutdown'}}}
    class Teleop:
        def __enter__(self): raise SafetyViolation('motion inhibited')
        def __exit__(self, *_): pass
    class Robot:
        name = 'synthetic'
        capabilities = frozenset({'teleop'})
        def __init__(self, _uri): pass
        def __enter__(self): return self
        def __exit__(self, *_): pass
        def teleop(self, **_kwargs): return Teleop()
    sdk = ModuleType('ringfour')
    sdk.Robot = Robot
    runtime = ModuleType('ringfour_runtime')
    runtime.logbook = SimpleNamespace(info=lambda message, **fields: entries.append((message, fields)))
    monkeypatch.setitem(sys.modules, 'ringfour', sdk)
    monkeypatch.setitem(sys.modules, 'ringfour_runtime', runtime)
    monkeypatch.setenv('RINGFOUR_AGENT_URL', 'ws://fixture')
    with pytest.raises(SafetyViolation, match='motion inhibited'):
        runpy.run_path(str(Path(__file__).resolve().parents[2] / 'missions/logbook-demo/main.py'))
    assert 'unclean shutdown' in capsys.readouterr().out
    assert entries[-1] == ('Wheel test failed', {'error_type': 'SafetyViolation', 'reason': 'quarantine'})


def test_safety_diagnostic_does_not_open_a_teleop_session(monkeypatch, capsys):
    calls = []
    class RingfourError(Exception):
        def __init__(self, message):
            super().__init__(message)
            self.details = {'reason': 'quarantine', 'resources': {'restart': {'reason': 'unclean shutdown'}}}
    class Teleop:
        def drive(self, **values):
            calls.append(values)
            raise RingfourError('motion inhibited')
        def __enter__(self): raise AssertionError('diagnostic must not open a teleop session')
    class Robot:
        name = 'synthetic'
        description_sha = 'a' * 64
        def __init__(self, _uri): pass
        def __enter__(self): return self
        def __exit__(self, *_): pass
        def teleop(self, **_kwargs): return Teleop()
        def get_estop_engaged(self): return True
    sdk = ModuleType('ringfour')
    sdk.Robot = Robot
    errors = ModuleType('ringfour.errors')
    errors.RingfourError = RingfourError
    monkeypatch.setitem(sys.modules, 'ringfour', sdk)
    monkeypatch.setitem(sys.modules, 'ringfour.errors', errors)
    monkeypatch.setenv('RINGFOUR_AGENT_URL', 'ws://fixture')
    runpy.run_path(str(Path(__file__).resolve().parents[2] / 'missions/logbook-demo/safety_diagnostic.py'))
    assert calls == [{'linear': 0.0, 'angular': 0.0}]
    output = capsys.readouterr().out
    assert 'unclean shutdown' in output and 'Inhibit engaged: True' in output
