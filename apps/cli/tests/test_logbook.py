from types import SimpleNamespace

from ringfour_cli import main
from typer.testing import CliRunner


def _event(index: int) -> dict:
    return {
        "event_id": f"event-{index}",
        "occurred_at": "2026-11-17T00:00:00Z",
        "type": "mission.logbook.entry",
        "robot_id": "robot-id",
        "execution_id": "execution-id",
        "actor_user_id": "actor-id",
        "payload": {"message": f"entry {index}"},
    }


def test_logbook_list_aggregates_api_pages(monkeypatch):
    calls = []
    printed = []
    rendered_rows = []
    table_type = main.Table

    class RecordingTable(table_type):
        def add_row(self, *renderables, **kwargs):
            rendered_rows.append(renderables)
            return super().add_row(*renderables, **kwargs)
    pages = {
        None: {"items": [_event(i) for i in range(100)], "next_cursor": "cursor-1"},
        "cursor-1": {"items": [_event(i) for i in range(100, 200)], "next_cursor": "cursor-2"},
        "cursor-2": {"items": [_event(i) for i in range(200, 250)], "next_cursor": "cursor-3"},
    }

    def request(method, path, settings, store, *, params):
        calls.append(params.copy())
        return SimpleNamespace(json=lambda: pages[params.get("cursor")])

    monkeypatch.setattr(main, "_settings", lambda: SimpleNamespace())
    monkeypatch.setattr(main, "_resolve_org", lambda settings, org: "org-id")
    monkeypatch.setattr(main.auth, "TokenStore", lambda: object())
    monkeypatch.setattr(main, "_api_request", request)
    monkeypatch.setattr(main, "Table", RecordingTable)
    monkeypatch.setattr(main.console, "print", lambda value, *args, **kwargs: printed.append(value))

    result = CliRunner().invoke(main.app, ["logbook", "list", "--limit", "250", "--robot", "robot-id"])

    assert result.exit_code == 0, result.output
    assert [call["limit"] for call in calls] == [100, 100, 50]
    assert [call.get("cursor") for call in calls] == [None, "cursor-1", "cursor-2"]
    assert all(call["robot_id"] == "robot-id" for call in calls)
    assert len(printed[0].rows) == 250
    assert rendered_rows[-1][-1].plain == "entry 249"
    assert "cursor-3" in printed[1]


def test_logbook_show_queries_and_prints_one_event(monkeypatch):
    expected = _event(7)
    calls = []

    def request(method, path, settings, store, *, params):
        calls.append((method, path, params))
        return SimpleNamespace(json=lambda: {"items": [expected]})

    monkeypatch.setattr(main, "_settings", lambda: SimpleNamespace())
    monkeypatch.setattr(main, "_resolve_org", lambda settings, org: "org-id")
    monkeypatch.setattr(main.auth, "TokenStore", lambda: object())
    monkeypatch.setattr(main, "_api_request", request)

    result = CliRunner().invoke(main.app, ["logbook", "show", expected["event_id"]])

    assert result.exit_code == 0, result.output
    assert calls == [("GET", "/v1/organizations/org-id/events", {"event_id": expected["event_id"], "limit": 1})]
    assert "entry 7" in result.output
