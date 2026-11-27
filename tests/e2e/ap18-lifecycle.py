#!/usr/bin/env python3
"""Disposable real API/Postgres/registry/Daemon/Engine acceptance; no live stack changes."""
import importlib.util
import json
import subprocess
import sys
import uuid
from pathlib import Path

import httpx
from ringfour_daemon.db import connect


def load(name):
    spec = importlib.util.spec_from_file_location(name.replace('-', '_'), Path(__file__).with_name(name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def configure(c):
    body = {"manifest": c["manifest"], "visibility": "listed"}
    assert c["client"].post(f'/v1/organizations/{c["org"]}/app-revisions', json=body, headers={"Idempotency-Key": str(uuid.uuid4())}).status_code == 403
    # The harness replaces its disposable API during its recovery scenario.
    c["api_env"]["Apps__OfficialPublisherOrganizationId"] = c["org"]


def install_for_consumer(c, consumer, org, revision):
    checked = c['checked']
    robot = checked(consumer.post(f'/v1/organizations/{org}/robots', json={"name": "AP18 consumer robot"}))['id']
    token = checked(consumer.post(f'/v1/robots/{robot}/enrollment-tokens'))['token']
    port = c['support'].port()
    env = c['daemon_env'] | {"DAEMON_DB_PATH": str(c['root'] / 'consumer.db'),
                             "DAEMON_CREDENTIAL_PATH": str(c['root'] / 'consumer.credential')}
    with (c['root'] / 'consumer-daemon.log').open('w+') as log:
        daemon = subprocess.Popen([sys.executable, '-m', 'uvicorn', 'ringfour_daemon.main:create_app', '--factory',
                                   '--host', '127.0.0.1', '--port', str(port)], env=env, stdout=log, stderr=subprocess.STDOUT)
        try:
            origin = f'http://127.0.0.1:{port}'
            c['support'].wait_for(lambda: httpx.get(origin + '/v1/identity').status_code == 200, 'consumer daemon ready')
            checked(httpx.post(origin + '/v1/enroll', json={"token": token}))
            c['support'].wait_for(lambda: (checked(consumer.get(f'/v1/robots/{robot}/app-status')).get('inventory') or {}).get('state') == 'available', 'consumer inventory')
            response = consumer.post(f'/v1/robots/{robot}/app-installations', json={"revision_id": revision['id'], "share_id": None,
                                      "configuration": {"sample_count": 1}}, headers={"Idempotency-Key": "consumer-install"})
            accepted = checked(response)
            def terminal():
                operation = checked(consumer.get('/v1/app-operations/' + accepted['operation_id']))
                return operation if operation['state'] in ('succeeded', 'failed') else None
            assert c['support'].wait_for(terminal, 'consumer installed')['state'] == 'succeeded'
            return accepted['installation_id']
        except BaseException:
            log.flush(); log.seek(0)
            print(log.read()[-6000:], file=sys.stderr)
            raise
        finally:
            c['support'].terminate(daemon)


def accept(c):
    client, checked, post, completed = (c[k] for k in ("client", "checked", "post", "completed"))
    public = json.loads(json.dumps(c["manifest"]))
    public["release"] = "0.9.0"
    release = checked(post(f'/v1/organizations/{c["org"]}/app-revisions', {"manifest": public, "visibility": "listed"}))
    with httpx.Client(base_url=str(client.base_url), headers={"Authorization": "Bearer " + load("ap17-settings").signed_token(c["pem"])}, trust_env=False) as consumer:
        org = checked(consumer.post('/v1/organizations', json={"name": "AP18 consumer"}))["id"]
        page = checked(consumer.get('/v1/store/apps', params={"organization_id": org, "q": public["app_key"]}))
        assert any(item['id'] == release['app_id'] and item['publisher_type'] == 'ringfour' for item in page['items'])
        assert consumer.get('/v1/app-revisions/' + c['revision']['id']).status_code == 404
        imported = consumer.post(f'/v1/organizations/{org}/app-imports', json={"revision_id": release['id'], "share_id": None}, headers={"Idempotency-Key": "official-import"})
        checked(imported)
        consumer_installation = install_for_consumer(c, consumer, org, release)
        consumer_headers = dict(consumer.headers)
    resource = c['resource']
    binding = checked(client.get(resource))['installed']
    history = checked(client.get(resource + '/configuration-revisions'))['items']
    assert any(item['id'] == binding['configuration_revision_id'] for item in history)
    assert checked(client.get(f'/v1/robots/{c["robot"]}/app-status'))['supports_app_uninstall'] is True
    accepted = c['cli']('uninstall', c['installation'], '--yes')
    assert completed(accepted)['state'] == 'succeeded'
    removed = checked(client.get(resource))
    assert removed['installed'] is None and removed['removed_at'] is not None
    assert checked(client.get(resource + '/configuration-revisions/' + binding['configuration_revision_id']))
    replacement = checked(post(c['path'], {"revision_id": release['id'], "share_id": None, "configuration": {"sample_count": 1}}))
    assert replacement['installation_id'] != c['installation']
    assert completed(replacement)['state'] == 'succeeded'
    # Inspect durable removal evidence alongside the replacement binding.
    # Component tests separately exercise old-command replay.
    with connect(c['db']) as db:
        old = db.execute("SELECT terminal_report_json FROM app_preparations WHERE operation_id=?", (accepted['operation_id'],)).fetchone()
        assert json.loads(old[0])['operations'][0]['state'] == 'succeeded'
        assert db.execute("SELECT revision_id FROM app_installations WHERE installation_id=?", (c['installation'],)).fetchone() == (None,)
        assert db.execute("SELECT revision_id FROM app_installations WHERE installation_id=?", (replacement['installation_id'],)).fetchone() == (release['id'],)
    assert checked(client.get('/v1/app-installations/' + c['other_install']['installation_id']))['installed'] is not None
    with httpx.Client(base_url=str(client.base_url), headers=consumer_headers, trust_env=False) as consumer:
        assert checked(consumer.get('/v1/app-installations/' + consumer_installation))['installed']['revision_id'] == release['id']
    print(json.dumps({"passed": True, "official_discovery_import_install_other_workspace": True, "private_isolation": True,
                      "cli_uninstall": True, "removed_configuration_retained": True, "reinstall_new_identity": True,
                      "other_installation_preserved": True, "update_rollback_recovery": True}), flush=True)


if __name__ == '__main__':
    load('ap07-install').main(run_acceptance=accept, configure=configure)
