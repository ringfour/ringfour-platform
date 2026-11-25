import {test} from "node:test";
import assert from "node:assert/strict";
import {readFile} from "node:fs/promises";
import Ajv2020 from "ajv/dist/2020.js";
import {canonicalJson, manifestDigest, validateManifest, validateConfiguration} from "./support/app-manifest.mjs";
import {validateResource} from "./support/apps-api.mjs";

const load = async name => JSON.parse(await readFile(new URL(`../fixtures/apps/v2/${name}.json`, import.meta.url)));
const authoringSchema = JSON.parse(await readFile(new URL("../schemas/apps/v2/authoring.schema.json", import.meta.url)));

test("v2 manifest and authoring documents validate while retaining strict versioning", async () => {
  const manifest = await load("manifest");
  assert.equal(validateManifest(manifest), manifest);
  assert.ok(validateResource("apps/v1/requests.schema.json#/$defs/publish", {manifest, visibility: "listed"}));
  const validateAuthoring = new Ajv2020({strict: true, allErrors: true, strictRequired: true}).compile(authoringSchema);
  assert.ok(validateAuthoring(await load("authoring")));
  assert.throws(() => validateManifest({...manifest, schema_version: 1}), /invalid_manifest/);
  assert.throws(() => validateManifest({...manifest, unexpected: true}), /invalid_manifest/);
});

test("v2 startup and live settings validate as complete independent objects", async () => {
  const manifest = await load("manifest");
  assert.deepEqual(validateConfiguration(manifest, {sample_count: 20}), {sample_count: 20});
  assert.deepEqual(validateConfiguration({configuration: manifest.runtime_settings}, {interval_ms: 50}), {interval_ms: 50});
  assert.throws(() => validateConfiguration({configuration: manifest.runtime_settings}, {interval_ms: "50"}), /invalid_config/);
  assert.throws(() => validateManifest({...manifest, runtime_settings: {...manifest.runtime_settings, defaults: {interval_ms: 50, extra: true}}}), /invalid_config/);
});

test("v2 environment mappings are declared, bounded and reserved names are refused", async t => {
  const manifest = await load("manifest");
  for (const fixture of await load("rejected")) {
    await t.test(fixture.name, () => assert.throws(() => validateManifest(fixture.manifest)));
  }
  const optional = structuredClone(manifest);
  optional.configuration.schema.required = [];
  optional.configuration.environment = {SAMPLE_COUNT: "sample_count"};
  assert.ok(validateManifest(optional));
  const nulValue = structuredClone(manifest);
  nulValue.configuration.schema.properties.note = {type: "string"};
  nulValue.configuration.environment.APP_NOTE = "note";
  assert.throws(() => validateConfiguration(nulValue, {sample_count: 3, note: "bad\0value"}), /invalid_environment_value/);
  const tooMany = structuredClone(manifest);
  tooMany.configuration.environment = Object.fromEntries(Array.from({length: 33}, (_, i) => [`APP_VALUE_${i}`, "sample_count"]));
  assert.throws(() => validateManifest(tooMany), /invalid_manifest|invalid_environment_mapping/);
});

test("v2 canonical bytes and digest bind its optional declarations", async () => {
  const manifest = await load("manifest");
  const canonical = canonicalJson(manifest);
  const digest = manifestDigest(manifest);
  const golden = await load("canonical");
  assert.equal(digest, golden.digest);
  assert.equal(canonical, golden.json);
});
