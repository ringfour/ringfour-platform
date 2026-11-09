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
  assert.ok(error(await loadJson("fixtures/errors/v1/unauthenticated.json")), "unauthenticated fixture");
  assert.ok(error(await loadJson("fixtures/errors/v1/forbidden.json")), "forbidden fixture");
  assert.ok(error(await loadJson("fixtures/errors/v1/last-owner.json")), "last-owner fixture");
});

test("identity fixtures validate against the v1 identity schemas", async () => {
  const me = await validatorFor("schemas/identity/v1/me-response.schema.json");
  const organization = await validatorFor("schemas/identity/v1/organization.schema.json");
  const organizations = await validatorFor("schemas/identity/v1/organization-list-response.schema.json");
  const members = await validatorFor("schemas/identity/v1/member-list-response.schema.json");

  assert.ok(me(await loadJson("fixtures/identity/v1/me.json")), "me fixture");
  assert.ok(organization(await loadJson("fixtures/identity/v1/organization.json")), "organization fixture");
  assert.ok(organizations(await loadJson("fixtures/identity/v1/organizations-list.json")), "organizations fixture");
  assert.ok(members(await loadJson("fixtures/identity/v1/members-list.json")), "members fixture");
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

test("identity schemas reject malformed documents", async () => {
  const user = await validatorFor("schemas/identity/v1/user.schema.json");
  const membership = await validatorFor("schemas/identity/v1/membership.schema.json");
  const createOrg = await validatorFor("schemas/identity/v1/create-organization-request.schema.json");
  const addMember = await validatorFor("schemas/identity/v1/add-member-request.schema.json");
  const organizations = await validatorFor("schemas/identity/v1/organization-list-response.schema.json");

  assert.equal(
    user({ id: "not-a-uuid", created_at: "2026-11-09T08:15:30Z" }),
    false,
    "user with malformed id",
  );
  assert.equal(
    user({ id: "3f6c1e2a-7b4d-4e5f-9a0b-1c2d3e4f5a6b", created_at: "2026-11-09 08:15:30" }),
    false,
    "user with non-RFC3339 timestamp",
  );
  assert.equal(
    user({ id: "3f6c1e2a-7b4d-4e5f-9a0b-1c2d3e4f5a6b", created_at: "2026-11-09T08:15:30Z", auth0_sub: "x" }),
    false,
    "user leaking the external subject",
  );
  assert.equal(
    membership({
      organization_id: "9b8a7c6d-5e4f-4a3b-8c7d-6e5f4a3b2c1d",
      user_id: "3f6c1e2a-7b4d-4e5f-9a0b-1c2d3e4f5a6b",
      role: "admin",
      created_at: "2026-11-09T08:16:02Z",
    }),
    false,
    "membership with a role outside the matrix",
  );
  assert.equal(createOrg({ name: "   " }), false, "blank organization name");
  assert.equal(createOrg({ name: "Acme", extra: 1 }), false, "create request with extra property");
  assert.equal(
    addMember({ user_id: "3f6c1e2a-7b4d-4e5f-9a0b-1c2d3e4f5a6b" }),
    false,
    "add member without role",
  );
  assert.equal(organizations({ items: [] }), false, "list response without next_cursor");
  assert.equal(
    organizations({
      items: [
        { id: "9b8a7c6d-5e4f-4a3b-8c7d-6e5f4a3b2c1d", name: "Acme", created_at: "2026-11-09T08:16:02Z" },
      ],
      next_cursor: null,
    }),
    false,
    "organization list item without the caller's role",
  );
});
