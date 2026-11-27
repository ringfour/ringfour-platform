import httpx
from typer.testing import CliRunner

from ringfour_cli.main import app, collect_health

runner = CliRunner()

OK_BODY = {"status": "ok", "version": "0.1.0", "checks": {"postgresql": "ok"}}
FAIL_BODY = {"status": "fail", "version": "0.1.0", "checks": {"postgresql": "fail"}}


def make_factory(handler) -> object:
    return lambda: httpx.Client(transport=httpx.MockTransport(handler))


def all_ok(request: httpx.Request) -> httpx.Response:
    return httpx.Response(200, json=OK_BODY)


def test_collect_health_all_ok():
    results = collect_health(
        make_factory(all_ok), {"api": "http://api", "daemon": "http://daemon"}
    )
    assert all(r.status == "ok" for r in results)
    assert results[0].checks == {"postgresql": "ok"}


def test_collect_health_marks_503_as_fail():
    def handler(request: httpx.Request) -> httpx.Response:
        if "api" in str(request.url):
            return httpx.Response(503, json=FAIL_BODY)
        return httpx.Response(200, json=OK_BODY)

    results = collect_health(
        make_factory(handler), {"api": "http://api", "daemon": "http://daemon"}
    )
    assert results[0].status == "fail"
    assert results[0].reachable
    assert results[1].status == "ok"


def test_collect_health_marks_unreachable():
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("connection refused", request=request)

    results = collect_health(make_factory(handler), {"api": "http://api"})
    assert results[0].status == "fail"
    assert not results[0].reachable


def test_health_command_exits_zero_when_all_ok(monkeypatch):
    monkeypatch.setattr("ringfour_cli.main._default_client_factory", make_factory(all_ok))
    result = runner.invoke(app, ["health"])
    assert result.exit_code == 0
    assert "api" in result.output
    assert "daemon" in result.output


def test_health_command_exits_one_on_failure(monkeypatch):
    def handler(request: httpx.Request) -> httpx.Response:
        if "8081" in str(request.url):
            raise httpx.ConnectError("connection refused", request=request)
        return httpx.Response(200, json=OK_BODY)

    monkeypatch.setattr("ringfour_cli.main._default_client_factory", make_factory(handler))
    result = runner.invoke(app, ["health"])
    assert result.exit_code == 1
    assert "unreachable" in result.output
