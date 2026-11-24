import os,time,json
from pathlib import Path
from ringfour_runtime import logbook,artifacts,interactions
p=Path(os.environ['RINGFOUR_ARTIFACT_DIR'])/'proof.bin'
p.write_bytes(b'original immutable evidence')
a=artifacts.publish_file(p)
p.write_bytes(b'changed after publication')
p.unlink()
e=logbook.info('Runtime acceptance',evidence=[a],sample_count=1)
i=interactions.create('Continue runtime acceptance?',{'yes':'Yes','no':'No'})
print(json.dumps({'event':'runtime.ready','artifact_id':a,'entry_id':e,'request_id':i.request_id}),flush=True)
answer=interactions.wait(i.request_id,poll_seconds=1)
print(json.dumps({'event':'runtime.answered','outcome':answer.outcome}),flush=True)
