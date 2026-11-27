import json
import os
import sqlite3
from dataclasses import replace
from pathlib import Path

import httpx
import pytest

from ringfour_daemon.config import Settings
from ringfour_daemon.db import migrate
from ringfour_daemon.enrollment import load_identity, save_identity
from ringfour_daemon.release import recover_released_enrollment

ROBOT = "11111111-1111-4111-8111-111111111111"
CREDENTIAL = "22222222-2222-4222-8222-222222222222"
ORG = "33333333-3333-4333-8333-333333333333"


@pytest.fixture
def enrolled(tmp_path):
    settings = Settings(
        db_path=str(tmp_path / "daemon.db"),
        credential_path=str(tmp_path / "secret"),
        platform_url="http://platform",
    )
    migrate(settings.db_path)
    assert save_identity(
        settings,
        {
            "robot_id": ROBOT,
            "organization_id": ORG,
            "credential_id": CREDENTIAL,
            "platform_url": "http://platform",
        },
        "fixture-device-secret",
    )
    return settings, load_identity(settings.db_path)


def client_factory(receipt, status=200):
    def handler(request):
        assert request.url.path == "/v1/device/enrollment-state"
        assert json.loads(request.content) == {
            "credential_id": CREDENTIAL,
            "secret": "fixture-device-secret",
        }
        return httpx.Response(status, json=receipt)

    return lambda: httpx.Client(transport=httpx.MockTransport(handler))


def released(**changes):
    return {
        "state": "released",
        "reason": "robot_unenrolled",
        "robot_id": ROBOT,
        "credential_id": CREDENTIAL,
        **changes,
    }


def test_release_archives_old_binding_then_allows_fresh_pairing_without_old_state(
    enrolled,
):
    settings, old = enrolled
    archive = recover_released_enrollment(settings, client_factory(released()))
    assert archive and archive.is_file()
    assert os.stat(archive).st_mode & 0o777 == 0o600
    assert os.stat(archive.parent).st_mode & 0o777 == 0o700
    assert load_identity(str(archive)) == old
    assert load_identity(settings.db_path) is None
    assert not Path(settings.credential_path).exists()
    with sqlite3.connect(settings.db_path) as conn:
        assert conn.execute("SELECT count(*) FROM app_runs").fetchone()[0] == 0
        assert conn.execute("SELECT count(*) FROM schema_migrations").fetchone()[0] > 0
    assert save_identity(
        settings,
        {
            "robot_id": "44444444-4444-4444-8444-444444444444",
            "organization_id": "55555555-5555-4555-8555-555555555555",
            "credential_id": "66666666-6666-4666-8666-666666666666",
            "platform_url": "http://platform",
        },
        "new-secret",
    )
    assert (
        load_identity(settings.db_path)["deployment_namespace"]
        != old["deployment_namespace"]
    )
    assert load_identity(str(archive))["organization_id"] == ORG


@pytest.mark.parametrize(
    "receipt,status",
    [
        ({"state": "enrolled"}, 200),
        ([], 200),
        (released(), 401),
        (released(), 404),
        (released(robot_id="different"), 200),
        (released(credential_id="different"), 200),
        (released(reason="credential_revoked"), 200),
    ],
)
def test_normal_revocation_errors_and_wrong_receipts_do_not_clear_identity(
    enrolled, receipt, status
):
    settings, old = enrolled
    assert (
        recover_released_enrollment(settings, client_factory(receipt, status)) is None
    )
    assert load_identity(settings.db_path) == old
    assert Path(settings.credential_path).read_text() == "fixture-device-secret"


def test_other_app_writer_prevents_reset(enrolled):
    import fcntl

    settings, old = enrolled
    descriptor = os.open(settings.db_path + ".apps.lock", os.O_CREAT | os.O_RDWR, 0o600)
    try:
        fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        assert recover_released_enrollment(settings, client_factory(released())) is None
        assert load_identity(settings.db_path) == old
    finally:
        os.close(descriptor)


def test_remaining_owned_containers_prevent_archiving_and_rebinding(
    enrolled, monkeypatch
):
    from ringfour_daemon import app_engine

    settings, old = enrolled

    class Engine:
        def __init__(self, _settings):
            pass

        def owned(self, robot, namespace):
            assert (robot, namespace) == (ROBOT, old["deployment_namespace"])
            return [{"Id": "still-owned"}]

        def close(self):
            pass

    monkeypatch.setattr(app_engine, "Engine", Engine)
    assert (
        recover_released_enrollment(
            replace(settings, apps_enabled=True), client_factory(released())
        )
        is None
    )
    assert load_identity(settings.db_path) == old
