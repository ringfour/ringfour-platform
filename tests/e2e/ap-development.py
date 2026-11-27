#!/usr/bin/env python3
"""Disposable real App fastlane: TLS CLI -> Daemon -> Docker, API/PG provenance."""
import importlib.util
import json
import subprocess
import sys
from pathlib import Path

from ringfour_daemon.development_tls import local_identity


def configure(c):
    root, support, env = c['root'], c['support'], c['daemon_env']
    port = support.port(); pem = root / 'development.pem'; local_identity(str(pem))
    env.update(RINGFOUR_DEVELOPMENT_URL=f'https://127.0.0.1:{port}',RINGFOUR_DEVELOPMENT_TLS_PATH=str(pem))
    stream=(root/'development-listener.log').open('w')
    process=subprocess.Popen([sys.executable,'-m','uvicorn','ringfour_daemon.development:local_app','--factory','--host','127.0.0.1','--port',str(port),'--ssl-certfile',str(pem),'--ssl-keyfile',str(pem)],env=env,stdout=stream,stderr=subprocess.STDOUT)
    def cleanup():
        support.terminate(process);stream.close()
    return cleanup


def acceptance(c):
    client, post, checked, support, cli = [c[k] for k in ('client','post','checked','support','cli')]
    root=c['root'];project=root/'development-app'
    cli('init',project,'--app-key','dev-probe','--repository',c['host']+'/apps/dev-probe')
    (project/'main.py').write_text('import time\nfrom pathlib import Path\nPath("scratch.txt").write_text("owned workspace")\nprint("generation.one",flush=True)\nwhile True: time.sleep(.1)\n')
    cli('build',project,'--development'); revision=cli('publish',project)
    installed=checked(post(c['path'],{'revision_id':revision['id'],'share_id':None,'configuration':{'sample_count':1}}))
    assert c['completed'](installed)['state']=='succeeded'
    robot=c['robot']; endpoint=f'/v1/robots/{robot}/app-development'
    support.wait_for(lambda: client.post(endpoint,json={'installation_id':installed['installation_id'],'ttl_seconds':3600},headers={'Idempotency-Key':'e2e-dev-open'}).status_code==201,'development endpoint advertised')
    def dev(*args):
        result=subprocess.run([str(Path(__file__).resolve().parents[2]/'apps/cli/.venv/bin/ringfour-cli'),'app','dev',*map(str,args)],cwd=project,env=c['cli_env'],capture_output=True,text=True,timeout=90)
        assert result.returncode==0,result.stdout+result.stderr
        return result.stdout
    dev('connect','--robot',robot);dev('sync');dev('restart')
    def run():
        context=json.loads((project/'.ringfour/development.json').read_text())
        return checked(client.get('/v1/app-runs/'+context['run_id']))
    support.wait_for(lambda:run()['state']=='running','first source started in real App container')
    first=run();assert first['snapshot']['development']['generation']==1
    support.wait_for(lambda:'generation.one' in dev('logs'),'first source stdout')
    before=sum('/images/create' in r for r in c['requests'])
    (project/'main.py').write_text('import time\nfrom pathlib import Path\nassert not Path("scratch.txt").exists()\nprint("generation.two",flush=True)\nwhile True: time.sleep(.1)\n')
    dev('sync');dev('restart')
    support.wait_for(lambda:run()['state']=='running','second source started')
    second=run();assert second['id']!=first['id'];assert second['snapshot']['image']==first['snapshot']['image']
    assert second['snapshot']['development']['generation']==2
    assert second['snapshot']['development']['archive_sha256']!=first['snapshot']['development']['archive_sha256']
    support.wait_for(lambda:'generation.two' in dev('logs'),'second source stdout')
    assert sum('/images/create' in r for r in c['requests'])==before
    assert post('/v1/app-installations/'+installed['installation_id']+'/start',{'expected_revision_id':second['snapshot']['revision_id'],'expected_configuration_revision_id':second['snapshot']['configuration_revision_id']}).status_code==409
    dev('stop')
    support.wait_for(lambda:run()['state'] not in ('accepted','running'),'session closure stops development App')
    assert checked(client.get(endpoint))['session'] is None
    print(json.dumps({'app_development':True,'generations':2,'image_rebuilt_for_source_edit':False,'source_attribution':True,'normal_app_logs':True,'end_session_stops_app':True},indent=2))


def main():
    spec=importlib.util.spec_from_file_location('fastlane_support',Path(__file__).with_name('ap07-install.py'))
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
    module.main(acceptance,configure)


if __name__=='__main__':main()
