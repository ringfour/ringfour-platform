import { test } from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { validateManifest, validateConfiguration, manifestDigest, canonicalJson } from "./support/app-manifest.mjs";

const load = async name => JSON.parse(await readFile(new URL(`../fixtures/apps/v1/${name}.json`, import.meta.url)));
const manifest = await load("manifest");

test("App manifest and canonical digest are portable contract fixtures", async () => {
  assert.equal(validateManifest(manifest), manifest);
  const golden = await load("canonical");
  assert.equal(canonicalJson(manifest), golden.json);
  assert.equal(manifestDigest(manifest), golden.digest);
  const reordered = Object.fromEntries(Object.entries(manifest).reverse());
  reordered.configuration.defaults = { sample_count: 1 };
  assert.equal(manifestDigest(reordered), golden.digest);
});

test("rejected App fixtures exercise structural and semantic validation", async t => {
  for (const fixture of await load("rejected")) {
    await t.test(fixture.name, () => assert.throws(() => validateManifest(fixture.manifest), new RegExp(fixture.error)));
  }
});

test("configuration is validated without coercion, implicit defaults or mutation", () => {
  for (const values of [{}, {sample_count: "2"}, {sample_count: true}, {sample_count: 1.5}, {sample_count: 0}, {sample_count: 21}, {sample_count: 1, secret: "x"}]) {
    assert.throws(() => validateConfiguration(manifest, values), /invalid_config/);
  }
  const values = {sample_count: 3};
  assert.deepEqual(validateConfiguration(manifest, values), values);
  assert.deepEqual(values, {sample_count: 3});
});

test("supported config types, enums and universal bounds", () => {
  const input = structuredClone(manifest);
  input.configuration = {schema: {type: "object", additionalProperties: false, properties: {
    label: {type: "string", minLength: 1, maxLength: 8, enum: ["check", "verify"]},
    verbose: {type: "boolean"}, count: {type: "integer"}
  }, required: ["label", "verbose"]}, defaults: {label: "check", verbose: false}};
  assert.ok(validateManifest(input));
  assert.ok(validateConfiguration(input, {label: "verify", verbose: true, count: 9007199254740991}));
  assert.throws(() => validateConfiguration(input, {label: "other", verbose: true}), /invalid_config/);
  assert.throws(() => validateConfiguration(input, {label: "check", verbose: "false"}), /invalid_config/);
  assert.throws(() => validateConfiguration(input, {label: "check", verbose: true, count: 9007199254740992}), /invalid_config/);
  input.configuration.schema.properties.label = {type: "string"};
  assert.throws(() => validateConfiguration(input, {label: "x".repeat(4097), verbose: true}), /invalid_config/);
  assert.throws(() => validateConfiguration(input, {label: "x".repeat(16384), verbose: true}), /config_too_large/);
});

test("manifest size is measured in UTF-8 bytes before structural validation", () => {
  const input = structuredClone(manifest);
  input.display.description = "🤖".repeat(16384);
  assert.throws(() => validateManifest(input), /manifest_too_large/);
});

test("arm instance predicate and multiple unambiguous targets", () => {
  const input = structuredClone(manifest);
  input.capabilities.required = [{capability: "groups", instance_kind: "arm"}];
  input.targets.push({...input.targets[0], architecture: "arm64"});
  assert.ok(validateManifest(input));
});

test("Unicode text preserves code points and rejects unpaired surrogates", () => {
  const input = structuredClone(manifest);
  input.display.name = "Robot 🤖";
  assert.ok(validateManifest(input));
  input.display.name = "\ud800";
  assert.throws(() => validateManifest(input), /invalid_unicode/);
  const config = structuredClone(manifest);
  config.configuration.schema.properties.sample_count = {type: "string", maxLength: 1};
  config.configuration.defaults.sample_count = "🤖";
  assert.ok(validateManifest(config));
  assert.throws(() => validateConfiguration(config, {sample_count: "\udfff"}), /invalid_unicode/);
});
