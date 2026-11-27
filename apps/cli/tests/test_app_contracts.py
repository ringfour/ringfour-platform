import json
from dataclasses import asdict
from pathlib import Path

import pytest

from ringfour_cli.app_contracts import read_app_failure, read_app_operation

FIXTURES = json.loads(
    (
        Path(__file__).resolve().parents[3]
        / "packages/contracts/fixtures/apps/v1/summaries.json"
    ).read_text()
)


@pytest.mark.parametrize("fixture", FIXTURES, ids=lambda f: f["name"])
def test_shared_response_summaries(fixture):
    read = read_app_operation if fixture["kind"] == "operation" else read_app_failure
    assert asdict(read(fixture["input"])) == fixture["expected"]


def test_ack_is_not_success_and_unknown_states_fail_closed():
    ack = next(f["input"] for f in FIXTURES if f["name"] == "install_ack")
    for patch in [
        {"state": "invented"},
        {"state": "succeeded"},
        {"phase": "observed_running"},
    ]:
        with pytest.raises(ValueError):
            read_app_operation({**ack, **patch})
