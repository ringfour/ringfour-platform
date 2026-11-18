from datetime import UTC, datetime, timedelta

import pytest
from ringfour_runtime import interactions


def document(state="pending", *, deadline=None, timeout_outcome=None, outcome=None):
    return {
        "request_id": "8ad8de90-65d7-43f5-b2be-8d0639b62e32",
        "execution_id": "5639b7d4-2219-4f8c-9404-d208615c6ce4",
        "state": state,
        "outcome": outcome,
        "deadline": deadline,
        "timeout_outcome": timeout_outcome,
        "response": None,
    }


def test_create_defaults_to_no_deadline(monkeypatch):
    sent = {}

    def call(method, path, body=None):
        sent.update(body)
        return document()

    monkeypatch.setattr(interactions, "_call", call)
    item = interactions.create("Continue?", {"yes": "Yes", "no": "No"})
    assert item.state == "pending"
    assert sent["deadline"] is None
    assert sent["timeout_outcome"] is None


def test_create_requires_deadline_and_timeout_outcome_together():
    deadline = datetime.now(UTC) + timedelta(minutes=1)
    with pytest.raises(interactions.InteractionError, match="provided together"):
        interactions.create("Continue?", {"yes": "Yes", "no": "No"}, timeout_outcome="no")
    with pytest.raises(interactions.InteractionError, match="provided together"):
        interactions.create("Continue?", {"yes": "Yes", "no": "No"}, deadline=deadline)


def test_wait_polls_until_answer_without_a_deadline(monkeypatch):
    replies = iter((document(), document("answered", outcome="yes")))
    callback_calls = []
    monkeypatch.setattr(interactions, "_call", lambda *_args: next(replies))

    result = interactions.wait("8ad8de90-65d7-43f5-b2be-8d0639b62e32",
                               on_timeout=callback_calls.append)

    assert result.state == "answered"
    assert callback_calls == []


def test_wait_invokes_timeout_callback_once_after_daemon_timeout(monkeypatch):
    expired = document("timed_out", deadline="2026-11-17T16:00:00Z",
                       timeout_outcome="abort", outcome="abort")
    monkeypatch.setattr(interactions, "_call", lambda *_args: expired)
    callback_calls = []

    result = interactions.wait("8ad8de90-65d7-43f5-b2be-8d0639b62e32",
                               on_timeout=callback_calls.append)

    assert result.state == "timed_out"
    assert callback_calls == [result]


def test_wait_retries_transient_runtime_outage(monkeypatch):
    calls = 0

    def call(*_args):
        nonlocal calls
        calls += 1
        if calls == 1:
            raise interactions.InteractionUnavailable("daemon restarting")
        return document("answered", outcome="continue")

    monkeypatch.setattr(interactions, "_call", call)
    monkeypatch.setattr(interactions.time, "sleep", lambda _seconds: None)
    result = interactions.wait("8ad8de90-65d7-43f5-b2be-8d0639b62e32")

    assert result.state == "answered"
    assert calls == 2
