import base64
import hashlib
import json
from types import SimpleNamespace

import httpx
import pytest
from typer.testing import CliRunner

from ringfour_cli import dev_sync, main
from ringfour_cli.mission_context import read_context


@pytest.fixture
def project(tmp_path, monkeypatch):
    (tmp_path / "mission.toml").write_text('schema_version = 1\nname = "hello"\nentrypoint = "main.py"\npython_requirement = ">=3.13,<3.14"\nlock_file = "lock.txt"\n')
    (tmp_path / "main.py").write_text("print('hello')\n")
    (tmp_path / "lock.txt").write_text("")
    (tmp_path / ".env").write_text("secret")
    (tmp_path / "__pycache__").mkdir()
    (tmp_path / "__pycache__" / "junk.pyc").write_bytes(b"not code")
    monkeypatch.chdir(tmp_path)
    monkeypatch.setattr(main, "_settings", lambda: SimpleNamespace(api_url="https://platform.example"))
    monkeypatch.setattr(main, "_resolve_org", lambda *_: "org")
    monkeypatch.setattr(main, "_target_robot", lambda *_: ("22222222-2222-4222-8222-222222222222", "robot"))
    monkeypatch.setattr(main.auth, "TokenStore", lambda: object())
    monkeypatch.setattr(main.time, "sleep", lambda _: None)
    return tmp_path


def test_start_sync_status_and_stop_use_project_context(project, monkeypatch):
    session = "11111111-1111-4111-8111-111111111111"
    operation = "33333333-3333-4333-8333-333333333333"
    state = {"generation": 0, "paths": [], "archive_sha256": None}
    submitted = []
    grants = []

    def api(method, path, _settings, _store, **kwargs):
        if path.endswith("/dev-sessions") and method == "POST":
            return httpx.Response(202, json={"session_id": session, "operation_id": operation, "execution_id": None})
        if path.endswith("/local-grants"):
            grant = kwargs["json"]
            grants.append(grant)
            return httpx.Response(202, json={"grant_id": grant["grant_id"], "operation_id": operation,
                "certificate_sha256": "a" * 64, "daemon_url": None})
        if "/operations/" in path:
            return httpx.Response(200, json={"state": "succeeded"})
        if path == f"/v1/dev-sessions/{session}" and method == "GET":
            return httpx.Response(200, json={"state": "ready", "robot_id": "22222222-2222-4222-8222-222222222222"})
        if path == f"/v1/dev-sessions/{session}" and method == "DELETE":
            return httpx.Response(202, json={"session_id": session, "operation_id": operation, "execution_id": None})
        raise AssertionError((method, path))

    def local(url, pin, token, method, _path, **kwargs):
        assert url == "https://robot.local:8084" and pin == "a" * 64
        assert hashlib.sha256(base64.urlsafe_b64decode(token + "=")).hexdigest() == grants[-1]["token_sha256"]
        if method == "GET": return 200, dict(state)
        body = kwargs["body"]
        submitted.append(body)
        state.update(generation=body["base_generation"] + 1, paths=[f["path"] for f in body["manifest"]["files"]], archive_sha256=body["archive_sha256"])
        return 200, {"session_id": session, "generation": state["generation"], "archive_sha256": body["archive_sha256"]}

    monkeypatch.setattr(main, "_api_request", api)
    monkeypatch.setattr(main, "local_request", local)
    cli = CliRunner()
    started = cli.invoke(main.app, ["dev", "start", "--daemon-url", "https://robot.local:8084"])
    assert started.exit_code == 0, started.output
    document = read_context(project)
    assert document["dev_sessions"][0]["session_id"] == session
    assert "token" not in json.dumps(document)
    synced = cli.invoke(main.app, ["dev", "sync"])
    assert synced.exit_code == 0, synced.output
    assert {item["path"] for item in submitted[0]["manifest"]["files"]} == {"main.py", "requirements.lock"}
    assert cli.invoke(main.app, ["dev", "sync"]).exit_code == 0
    assert len(submitted) == 1
    (project / "main.py").write_text("print('new')\n")
    assert cli.invoke(main.app, ["dev", "sync"]).exit_code == 0
    assert len(submitted) == 2 and submitted[1]["base_generation"] == 1
    assert cli.invoke(main.app, ["dev", "status"]).exit_code == 0
    assert cli.invoke(main.app, ["dev", "stop"]).exit_code == 0


def test_fingerprint_mismatch_never_sends_token(monkeypatch):
    called = []

    class Socket:
        def getpeercert(self, **_): return b"not-the-pinned-certificate"

    class Connection:
        sock = Socket()
        def __init__(self, *_args, **_kwargs): pass
        def connect(self): pass
        def request(self, *_args, **_kwargs): called.append(True)
        def close(self): pass

    monkeypatch.setattr(dev_sync.http.client, "HTTPSConnection", Connection)
    with pytest.raises(dev_sync.LocalConnectionError, match="fingerprint"):
        dev_sync.local_request("https://robot.local:8084", "a" * 64, "secret", "GET", "/v1/local/test")
    assert called == []


def test_snapshot_deletions_and_untrusted_urls(project):
    body = dev_sync.build_snapshot(project, ["old.py", "main.py", "requirements.lock"], 1)
    assert body["manifest"]["deleted_paths"] == ["old.py"]
    for url in ("http://robot", "https://user:secret@robot", "https://robot/path", "https://robot?q=token"):
        with pytest.raises(ValueError): dev_sync.daemon_url(url)


def test_expired_certificate_never_sends_token(monkeypatch):
    from datetime import UTC, datetime, timedelta

    from cryptography import x509
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import ec
    from cryptography.x509.oid import NameOID

    key = ec.generate_private_key(ec.SECP256R1())
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "test")])
    now = datetime.now(UTC)
    cert = (x509.CertificateBuilder().subject_name(name).issuer_name(name).public_key(key.public_key())
        .serial_number(x509.random_serial_number()).not_valid_before(now - timedelta(days=3))
        .not_valid_after(now - timedelta(days=1)).sign(key, hashes.SHA256()))
    der = cert.public_bytes(serialization.Encoding.DER)
    calls = []

    class Socket:
        def getpeercert(self, **_): return der

    class Connection:
        sock = Socket()
        def __init__(self, *_args, **_kwargs): pass
        def connect(self): pass
        def request(self, *_args, **_kwargs): calls.append(True)
        def close(self): pass

    monkeypatch.setattr(dev_sync.http.client, "HTTPSConnection", Connection)
    with pytest.raises(dev_sync.LocalConnectionError, match="expired"):
        dev_sync.local_request("https://robot.local:8084", hashlib.sha256(der).hexdigest(), "secret", "GET", "/v1/local/test")
    assert calls == []


def test_sync_retries_same_operation_after_lost_connection(project, monkeypatch):
    from ringfour_cli.mission_context import save_dev_session

    session = "11111111-1111-4111-8111-111111111111"
    robot = "22222222-2222-4222-8222-222222222222"
    operation = "33333333-3333-4333-8333-333333333333"
    save_dev_session(project, {"api_url": "https://platform.example", "organization_id": "org", "robot_id": robot,
        "robot_name": "robot", "session_id": session, "daemon_url": "https://robot.local:8084"})

    def api(method, path, _settings, _store, **kwargs):
        if path.endswith("/local-grants"):
            return httpx.Response(202, json={"grant_id": kwargs["json"]["grant_id"], "operation_id": operation,
                "certificate_sha256": "a" * 64, "daemon_url": None})
        if "/operations/" in path: return httpx.Response(200, json={"state": "succeeded"})
        return httpx.Response(200, json={"state": "ready", "robot_id": robot})

    requests = []
    def local(_url, _pin, _token, method, _path, **kwargs):
        if method == "GET": return 200, {"generation": 0, "paths": [], "archive_sha256": None}
        requests.append(kwargs)
        if len(requests) == 1: raise dev_sync.LocalTransportError("lost response")
        body = kwargs["body"]
        return 200, {"session_id": session, "generation": 1, "archive_sha256": body["archive_sha256"]}

    monkeypatch.setattr(main, "_api_request", api)
    monkeypatch.setattr(main, "local_request", local)
    result = CliRunner().invoke(main.app, ["dev", "sync"])
    assert result.exit_code == 0, result.output
    assert len(requests) == 2 and requests[0] == requests[1]


def test_restart_and_logs_use_local_output(project, monkeypatch):
    from ringfour_cli.mission_context import save_dev_session

    session = "11111111-1111-4111-8111-111111111111"
    robot = "22222222-2222-4222-8222-222222222222"
    execution = "44444444-4444-4444-8444-444444444444"
    save_dev_session(project, {"api_url": "https://platform.example", "organization_id": "org", "robot_id": robot,
        "robot_name": "robot", "session_id": session, "daemon_url": "https://robot.local:8084"})
    monkeypatch.setattr(main, "_local_grant", lambda *_: ("https://robot.local:8084", "a" * 64, "local-secret"))
    posted = []

    def api(method, _path, _settings, _store, **kwargs):
        if method == "POST":
            posted.append(kwargs["json"])
            return httpx.Response(202, json={"session_id": session, "execution_id": execution})
        return httpx.Response(200, json={"robot_id": robot, "state": "ready", "generation": 1, "last_execution_id": execution})

    def local(_url, _pin, _token, _method, path, **_kwargs):
        if path.endswith("/source"):
            return 200, {"generation": 1}
        if path.endswith(execution):
            return 200, {"execution_id": execution, "state": "succeeded"}
        stream = "stdout" if "stream=stdout" in path else "stderr"
        data = b"hello locally\n" if stream == "stdout" else b""
        return 200, {"execution_id": execution, "stream": stream, "offset": 0, "next_offset": len(data),
                     "data_base64": base64.b64encode(data).decode(), "eof": True, "truncated": False}

    monkeypatch.setattr(main, "_api_request", api)
    monkeypatch.setattr(main, "local_request", local)
    for command in (["dev", "restart", "--follow"], ["dev", "logs", "--follow"]):
        result = CliRunner().invoke(main.app, command)
        assert result.exit_code == 0, result.output
        assert "hello locally" in result.output
        assert "local-secret" not in result.output
    assert posted == [{"generation": 1}]


def test_restart_rejects_source_generation_mismatch(project, monkeypatch):
    from ringfour_cli.mission_context import save_dev_session

    session = "11111111-1111-4111-8111-111111111111"
    robot = "22222222-2222-4222-8222-222222222222"
    save_dev_session(project, {"api_url": "https://platform.example", "organization_id": "org", "robot_id": robot,
        "robot_name": "robot", "session_id": session, "daemon_url": "https://robot.local:8084"})
    monkeypatch.setattr(main, "_local_grant", lambda *_: ("https://robot.local:8084", "a" * 64, "secret"))
    monkeypatch.setattr(main, "local_request", lambda *_: (200, {"generation": 1}))
    def api(method, *_args, **_kwargs):
        assert method == "GET"
        return httpx.Response(200, json={"robot_id": robot, "state": "ready", "generation": 2})
    monkeypatch.setattr(main, "_api_request", api)
    result = CliRunner().invoke(main.app, ["dev", "restart"])
    assert result.exit_code == 1
    assert "generations do not match" in result.output
