import {test} from "node:test";
import assert from "node:assert/strict";
import {readFile} from "node:fs/promises";
import Ajv2020 from "ajv/dist/2020.js";
import {validateManifest} from "./support/app-manifest.mjs";

const schema = JSON.parse(await readFile(new URL("../schemas/apps/v1/authoring.schema.json", import.meta.url)));
const fixture = JSON.parse(await readFile(new URL("../fixtures/apps/v1/authoring.json", import.meta.url)));
const validate = new Ajv2020({strict: true, strictRequired: true}).compile(schema);

test("authoring repositories and build inputs are separate from final release images", () => {
  assert.ok(validate(fixture));
  assert.throws(() => validateManifest(fixture), /invalid_manifest/);
  const final = structuredClone(fixture);
  delete final.build; delete final.wheel_bundle;
  final.targets = final.targets.map(({repository, ...target}) => ({...target, image: `${repository}@sha256:${"a".repeat(64)}`}));
  assert.ok(validateManifest(final));
});

test("authoring rejects unsupported targets and immutable fields in editable repositories", () => {
  for (const mutation of [v => v.targets = [], v => v.targets[0].architecture = "x86_64",
    v => v.targets[0].repository += ":latest", v => v.targets[0].repository += "@sha256:" + "a".repeat(64),
    v => v.targets[0].image = "never", v => v.build.files = [], v => v.wheel_bundle.sha256 = "bad"]) {
    const value = structuredClone(fixture); mutation(value); assert.equal(validate(value), false);
  }
});
