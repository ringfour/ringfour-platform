import { test } from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import Ajv2020 from "ajv/dist/2020.js";

const root = new URL("../", import.meta.url);

async function loadJson(relativePath) {
  return JSON.parse(await readFile(new URL(relativePath, root), "utf8"));
}

const ajv = new Ajv2020({ allErrors: true, strict: true });
const validators = new Map();

async function validatorFor(relativePath) {
  if (!validators.has(relativePath)) {
    validators.set(relativePath, ajv.compile(await loadJson(relativePath)));
  }
  return validators.get(relativePath);
}

test("health fixtures validate against the v1 health schemas", async () => {
  const liveness = await validatorFor("schemas/health/v1/liveness-response.schema.json");
  const readiness = await validatorFor("schemas/health/v1/readiness-response.schema.json");

  assert.ok(liveness(await loadJson("fixtures/health/v1/liveness-ok.json")), "liveness-ok fixture");
  assert.ok(readiness(await loadJson("fixtures/health/v1/readiness-ok.json")), "readiness-ok fixture");
  assert.ok(readiness(await loadJson("fixtures/health/v1/readiness-fail.json")), "readiness-fail fixture");
});

test("error fixtures validate against the v1 error schema", async () => {
  const error = await validatorFor("schemas/errors/v1/error.schema.json");

  assert.ok(error(await loadJson("fixtures/errors/v1/invalid-input.json")), "invalid-input fixture");
  assert.ok(error(await loadJson("fixtures/errors/v1/not-found.json")), "not-found fixture");
  assert.ok(error(await loadJson("fixtures/errors/v1/conflict.json")), "conflict fixture");
});

test("schemas reject malformed documents", async () => {
  const liveness = await validatorFor("schemas/health/v1/liveness-response.schema.json");
  const readiness = await validatorFor("schemas/health/v1/readiness-response.schema.json");
  const error = await validatorFor("schemas/errors/v1/error.schema.json");

  assert.equal(liveness({ status: "ok" }), false, "liveness without version");
  assert.equal(liveness({ status: "ok", version: "0.1.0", extra: true }), false, "liveness with extra property");
  assert.equal(readiness({ status: "ok", version: "0.1.0", checks: {} }), false, "readiness without checks");
  assert.equal(
    readiness({ status: "ok", version: "0.1.0", checks: { postgresql: "degraded" } }),
    false,
    "readiness with a non-ok/fail check value",
  );
  assert.equal(error({ error: { code: "not_found", message: "x" } }), false, "error without request_id");
});
