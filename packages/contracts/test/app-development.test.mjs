import {test} from 'node:test';
import assert from 'node:assert/strict';
import {validateResource} from './support/apps-api.mjs';
const prefix='apps/development/protocol.schema.json#/$defs/';
const id='00000000-0000-0000-0000-000000000001';
test('App development binds a real installation and bounded lifetime',()=>{
 assert(validateResource(prefix+'open',{installation_id:id,ttl_seconds:3600}));
 for(const ttl_seconds of [0,299,28801,1.2])assert.throws(()=>validateResource(prefix+'open',{installation_id:id,ttl_seconds}));
 assert.throws(()=>validateResource(prefix+'open',{installation_id:id,ttl_seconds:3600,mount:'/var/run/docker.sock'}));
});
test('Source metadata has digest, operation and exact generations; source bytes stay local',()=>{
 const source={operation_id:id,base_generation:0,generation:1,archive_sha256:'sha256:'+'a'.repeat(64)};
 assert(validateResource(prefix+'source',source));
 assert.throws(()=>validateResource(prefix+'source',{...source,archive_base64:'not-hosted'}));
 assert.throws(()=>validateResource(prefix+'source',{...source,archive_sha256:'image:latest'}));
});
test('Local grants contain only a token digest',()=>{
 assert(validateResource(prefix+'grant',{session_id:id,token_sha256:'a'.repeat(64)}));
 assert.throws(()=>validateResource(prefix+'grant',{token:'secret'}));
});
