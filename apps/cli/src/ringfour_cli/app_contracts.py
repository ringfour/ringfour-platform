"""AP-01 typed App response summaries; CLI App commands arrive in AP-06/AP-12."""

import re
from dataclasses import dataclass
from datetime import datetime

STATES = {"queued", "accepted", "running", "succeeded", "failed", "cancelled"}
PHASES = {
    "queued",
    "accepted",
    "checking",
    "staging",
    "staged",
    "applied",
    "starting",
    "observed_running",
    "stopping",
    "stopped",
    "failed",
    "cancelled",
}


@dataclass(frozen=True)
class AppOperationSummary:
    operation_id: str
    state: str
    phase: str
    run_id: str | None
    observed_at: str | None
    failure_code: str | None


@dataclass(frozen=True)
class AppFailureSummary:
    code: str
    message: str
    retryable: bool


def _id(value):
    if not isinstance(value, str) or not re.fullmatch(
        r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", value
    ):
        raise ValueError("Invalid App ID")
    return value


def read_app_failure(value: object) -> AppFailureSummary:
    if not isinstance(value, dict) or not isinstance(value.get("details"), dict):
        raise TypeError("Invalid App failure")
    code, message, retryable = (
        value.get("code"),
        value.get("message"),
        value["details"].get("retryable"),
    )
    if (
        not isinstance(code, str)
        or not re.fullmatch(r"[a-z][a-z0-9_]*", code)
        or not isinstance(message, str)
        or not 1 <= len(message) <= 1000
        or not isinstance(retryable, bool)
    ):
        raise ValueError("Invalid App failure")
    return AppFailureSummary(code, message, retryable)


def read_app_operation(value: object) -> AppOperationSummary:
    if not isinstance(value, dict):
        raise TypeError("Invalid App response")
    state, phase = value.get("state"), value.get("phase")
    if (
        not isinstance(state, str)
        or state not in STATES
        or not isinstance(phase, str)
        or phase not in PHASES
    ):
        raise ValueError("Unknown App operation state or phase")
    observed = value["observed_at"]
    if observed is not None:
        if not isinstance(observed, str) or not re.fullmatch(
            r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,6})?Z", observed
        ):
            raise ValueError("Invalid App timestamp")
        datetime.fromisoformat(observed)
    failure = (
        None if value["failure"] is None else read_app_failure(value["failure"]).code
    )
    if (
        (state in {"queued", "accepted"} and (phase != state or observed is not None))
        or (
            state == "running"
            and (
                phase not in {"checking", "staging", "staged", "starting", "stopping"}
                or observed is None
            )
        )
        or (
            state == "succeeded"
            and (
                phase not in {"applied", "observed_running", "stopped"}
                or observed is None
            )
        )
        or (state == "failed" and (phase != "failed" or failure is None))
        or (state == "cancelled" and phase != "cancelled")
        or (state not in {"failed", "cancelled"} and failure is not None)
    ):
        raise ValueError("Inconsistent App observation")
    return AppOperationSummary(
        _id(value["id"]),
        state,
        phase,
        None if value["run_id"] is None else _id(value["run_id"]),
        observed,
        failure,
    )
