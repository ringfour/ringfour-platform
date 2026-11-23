import {test} from "node:test";
import assert from "node:assert/strict";
import {readFile} from "node:fs/promises";
import {validateResource} from "./support/apps-api.mjs";

const load = async name => JSON.parse(await readFile(new URL(`../fixtures/device/apps/v1/${name}.json`, import.meta.url)));
const examples = await load("positive");
const example = name => structuredClone(examples.find(f => f.name === name).value);
const path = name => `device/apps/v1/protocol.schema.json#/$defs/${name}`;

test("separately negotiated device payloads and legacy transport fixtures validate", async t => {
  for (const f of examples) await t.test(f.name, () => assert.ok(validateResource(f.schema, f.value)));
});
test("device negative fixtures reject ambiguous current inventory and unsafe commands", async t => {
  for (const f of await load("rejected")) await t.test(f.name, () => assert.throws(() => validateResource(f.schema, f.value), /invalid_resource/));
});
test("offline observations remain timestamped; unavailable inventory has no current reply", () => {
  const online = example("online_status"), offline = example("offline_last_observation");
  assert.notEqual(online.connectivity, offline.connectivity);
  assert.deepEqual(online.inventory, offline.inventory);
  const cached = example("agent_unavailable_with_cache");
  assert.equal(cached.inventory.state, "unavailable");
  assert.equal(cached.inventory.current, undefined);
  assert.deepEqual(cached.inventory.last_successful, online.inventory.current);
  assert.ok(validateResource(path("robot_app_status"), cached));
});
test("legacy support cannot be inferred from a normal v1 hello", async () => {
  const hello = JSON.parse(await readFile(new URL("../fixtures/device/v1/hello.json", import.meta.url)));
  const original = JSON.parse(await readFile(new URL("../schemas/device/v1/hello.schema.json", import.meta.url)));
  assert.equal(original.additionalProperties, false);
  assert.deepEqual(Object.keys(hello.payload).sort(), ["daemon_version", "supported_protocol_versions"]);
  assert.equal(example("legacy_daemon").support, "not_negotiated");
});
test("missing optional capabilities explain a compatible result; platform absence is distinct", () => {
  const optional = example("missing_optional_is_compatible");
  assert.equal(optional.status, "compatible");
  assert.ok(optional.missing_optional.length > 0);
  assert.ok(validateResource(path("compatibility"), optional));
  assert.throws(() => validateResource(path("compatibility"), {...optional, missing_required: [{capability: "imu"}]}), /invalid_resource/);
  assert.ok(validateResource(path("compatibility"), {...optional, status: "no_matching_image", target: null}));
});
