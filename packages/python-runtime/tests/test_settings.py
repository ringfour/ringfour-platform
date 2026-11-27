import threading

import pytest
from ringfour_runtime import settings

APPLICATION = "8ad8de90-65d7-43f5-b2be-8d0639b62e32"
REVISION = "5639b7d4-2219-4f8c-9404-d208615c6ce4"


def pending():
    return {
        "pending": {
            "application_id": APPLICATION,
            "settings_revision_id": REVISION,
            "values": {"sample_interval_ms": 250},
            "initial": False,
        }
    }


def test_result_retry_after_lost_ack_does_not_reapply(monkeypatch):
    calls = []
    handler_calls = []
    fail_result_once = True

    def call(method, path, body=None):
        nonlocal fail_result_once
        calls.append((method, path, body))
        if method == "GET":
            return pending()
        if fail_result_once:
            fail_result_once = False
            raise settings.SettingsUnavailable("ack was lost")
        return {"accepted": True, "application_id": APPLICATION}

    monkeypatch.setattr(settings, "_call", call)
    client = settings.SettingsClient()
    with pytest.raises(settings.SettingsUnavailable):
        client.poll_once(lambda values: handler_calls.append(dict(values)))
    result = client.poll_once(lambda values: handler_calls.append(dict(values)))

    assert handler_calls == [{"sample_interval_ms": 250}]
    assert result.outcome == "applied"
    assert [call[2] for call in calls if call[0] == "POST"] == [
        {"outcome": "applied"}, {"outcome": "applied"}
    ]


def test_app_validation_can_reject_before_handler_and_report_safe_reason(monkeypatch):
    reports = []

    def call(method, path, body=None):
        if method == "GET":
            return pending()
        reports.append(body)
        return {"accepted": True, "application_id": APPLICATION}

    handler_calls = []
    monkeypatch.setattr(settings, "_call", call)

    def validate(_values):
        raise settings.SettingsRejected("Unsupported interval.")

    result = settings.SettingsClient().poll_once(
        lambda values: handler_calls.append(values), validate=validate
    )

    assert result.outcome == "rejected"
    assert result.reason == "Unsupported interval."
    assert handler_calls == []
    assert reports == [{"outcome": "rejected", "reason": "Unsupported interval."}]


def test_settings_rejection_reason_must_be_safe_single_line():
    with pytest.raises(settings.SettingsError):
        settings.SettingsRejected("unsafe\nreason")


def test_serve_returns_immediately_when_cancelled():
    stop = threading.Event()
    stop.set()
    settings.SettingsClient().serve(lambda _values: None, stop_event=stop)


def test_serve_cancels_between_applications(monkeypatch):
    calls = []
    stop = threading.Event()

    def call(method, path, body=None):
        calls.append((method, path, body))
        return pending() if method == "GET" else {"accepted": True}

    monkeypatch.setattr(settings, "_call", call)
    settings.SettingsClient().serve(
        lambda _values: None,
        poll_seconds=30,
        stop_event=stop,
        on_application=lambda _application: stop.set(),
    )
    assert [call[0] for call in calls] == ["GET", "POST"]


def test_bounded_float_settings_values_keep_binary64_roundtrip():
    assert settings._json_number("0.2") == 0.2
    assert settings._json_number("-0.125") == -0.125
    assert settings._json_number("1.0") == 1
    assert settings._json_number("1e-7") == 1e-7
    for token in ("NaN", "Infinity", "1e400", "1e-400", "9007199254740992"):
        with pytest.raises(ValueError):
            settings._json_number(token)

