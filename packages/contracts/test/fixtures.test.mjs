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
  assert.ok(error(await loadJson("fixtures/errors/v1/invalid-token.json")), "invalid-token fixture");
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

test("robot fixtures validate against the v1 robots schemas", async () => {
  const robot = await validatorFor("schemas/robots/v1/robot.schema.json");
  const robots = await validatorFor("schemas/robots/v1/robot-list-response.schema.json");
  const enrollmentToken = await validatorFor("schemas/robots/v1/enrollment-token-response.schema.json");
  const enroll = await validatorFor("schemas/robots/v1/enroll-response.schema.json");
  const credential = await validatorFor("schemas/robots/v1/robot-credential.schema.json");
  const credentials = await validatorFor("schemas/robots/v1/credential-list-response.schema.json");
  const rotate = await validatorFor("schemas/robots/v1/rotate-credential-response.schema.json");
  const deviceIdentity = await validatorFor("schemas/robots/v1/device-authenticate-response.schema.json");

  assert.ok(robot(await loadJson("fixtures/robots/v1/robot.json")), "robot fixture");
  assert.ok(robots(await loadJson("fixtures/robots/v1/robots-list.json")), "robots-list fixture");
  assert.ok(enrollmentToken(await loadJson("fixtures/robots/v1/enrollment-token.json")), "enrollment-token fixture");
  assert.ok(enroll(await loadJson("fixtures/robots/v1/enroll.json")), "enroll fixture");
  assert.ok(credential(await loadJson("fixtures/robots/v1/credential.json")), "credential fixture");
  assert.ok(credentials(await loadJson("fixtures/robots/v1/credential-list.json")), "credential-list fixture");
  assert.ok(rotate(await loadJson("fixtures/robots/v1/rotate.json")), "rotate fixture");
  assert.ok(deviceIdentity(await loadJson("fixtures/robots/v1/device-identity.json")), "device-identity fixture");
});

test("robot schemas reject malformed documents", async () => {
  const robot = await validatorFor("schemas/robots/v1/robot.schema.json");
  const createRobot = await validatorFor("schemas/robots/v1/create-robot-request.schema.json");
  const enrollmentToken = await validatorFor("schemas/robots/v1/enrollment-token-response.schema.json");
  const enrollRequest = await validatorFor("schemas/robots/v1/enroll-request.schema.json");
  const enroll = await validatorFor("schemas/robots/v1/enroll-response.schema.json");
  const credential = await validatorFor("schemas/robots/v1/robot-credential.schema.json");
  const deviceAuth = await validatorFor("schemas/robots/v1/device-authenticate-request.schema.json");

  assert.equal(
    robot({
      id: "7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e",
      organization_id: "9b8a7c6d-5e4f-4a3b-8c7d-6e5f4a3b2c1d",
      name: "warehouse-01",
      created_at: "2026-11-09T08:20:11Z",
      profile_ref: null,
    }),
    false,
    "robot without enrolled_at",
  );
  assert.equal(
    robot({
      id: "7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e",
      organization_id: "9b8a7c6d-5e4f-4a3b-8c7d-6e5f4a3b2c1d",
      name: "warehouse-01",
      created_at: "2026-11-09T08:20:11Z",
      enrolled_at: null,
      profile_ref: null,
      secret_hash: "abc123",
    }),
    false,
    "robot leaking a credential hash",
  );
  assert.equal(createRobot({ name: "   " }), false, "blank robot name");
  assert.equal(createRobot({ name: "warehouse-01", token: "x" }), false, "create request with extra property");
  assert.equal(
    enrollmentToken({
      token_id: "2b3c4d5e-6f7a-4b8c-9d0e-1f2a3b4c5d6e",
      robot_id: "7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e",
      token: "rft_short",
      created_at: "2026-11-09T08:21:30Z",
      expires_at: "2026-11-09T08:36:30Z",
    }),
    false,
    "enrollment token with a wrong token shape",
  );
  assert.equal(enrollRequest({}), false, "enroll request without token");
  assert.equal(
    enroll({
      robot_id: "7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e",
      organization_id: "9b8a7c6d-5e4f-4a3b-8c7d-6e5f4a3b2c1d",
      credential_id: "3c4d5e6f-7a8b-4c9d-0e1f-2a3b4c5d6e7f",
      credential_secret: "rft_9N2dKmX4pQvR7sT1wYzA3bC6dE8fG0hI1jK4lM5nO6p",
    }),
    false,
    "enroll response with a token-shaped secret",
  );
  assert.equal(
    credential({
      id: "3c4d5e6f-7a8b-4c9d-0e1f-2a3b4c5d6e7f",
      robot_id: "7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e",
      created_at: "2026-11-09T08:22:40Z",
      expires_at: null,
      revoked_at: null,
      secret_hash: "deadbeef",
    }),
    false,
    "credential leaking its secret hash",
  );
  assert.equal(
    deviceAuth({ credential_id: "3c4d5e6f-7a8b-4c9d-0e1f-2a3b4c5d6e7f" }),
    false,
    "device authenticate without secret",
  );
});

test("device fixtures validate against the v1 device schemas", async () => {
  const envelope = await validatorFor("schemas/device/v1/envelope.schema.json");
  const payloads = {
    hello: await validatorFor("schemas/device/v1/hello.schema.json"),
    welcome: await validatorFor("schemas/device/v1/welcome.schema.json"),
    heartbeat: await validatorFor("schemas/device/v1/heartbeat.schema.json"),
    command: await validatorFor("schemas/device/v1/command.schema.json"),
    ack: await validatorFor("schemas/device/v1/ack.schema.json"),
    operation_status: await validatorFor("schemas/device/v1/operation-status.schema.json"),
    error: await validatorFor("schemas/device/v1/protocol-error.schema.json"),
  };

  for (const name of [
    "hello",
    "welcome",
    "heartbeat",
    "command",
    "ack",
    "operation-status-succeeded",
    "operation-status-interrupted",
    "protocol-error",
  ]) {
    const message = await loadJson(`fixtures/device/v1/${name}.json`);
    assert.ok(envelope(message), `${name} envelope`);
    assert.ok(payloads[message.type](message.payload), `${name} payload`);
  }
});

test("device schemas reject malformed documents", async () => {
  const envelope = await validatorFor("schemas/device/v1/envelope.schema.json");
  const hello = await validatorFor("schemas/device/v1/hello.schema.json");
  const welcome = await validatorFor("schemas/device/v1/welcome.schema.json");
  const command = await validatorFor("schemas/device/v1/command.schema.json");
  const operationStatus = await validatorFor("schemas/device/v1/operation-status.schema.json");
  const protocolError = await validatorFor("schemas/device/v1/protocol-error.schema.json");

  assert.equal(
    envelope({
      version: 1,
      message_id: "m1",
      type: "heartbeat",
      robot_id: "7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e",
      sequence: 1,
      payload: {},
      extra: true,
    }),
    false,
    "envelope with an extra property",
  );
  assert.equal(
    envelope({
      version: 2,
      message_id: "m1",
      type: "hello",
      robot_id: "7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e",
      sequence: 1,
      payload: {},
    }),
    false,
    "envelope with an unsupported protocol version",
  );
  assert.equal(
    hello({ daemon_version: "0.1.0", supported_protocol_versions: [] }),
    false,
    "hello without a supported protocol version",
  );
  assert.equal(
    welcome({
      server_time: "2026-11-10T09:00:00Z",
      heartbeat_interval_seconds: 0,
      session_id: "8d3e1f2a-9b4c-4d5e-8f6a-7b8c9d0e1f2a",
    }),
    false,
    "welcome with a zero heartbeat interval",
  );
  assert.equal(
    command({
      operation_id: "4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b",
      kind: "Bad Kind!",
      payload_version: 1,
      payload: {},
      deadline_at: "2026-11-10T09:01:00Z",
    }),
    false,
    "command with a kind outside the allowed shape",
  );
  assert.equal(
    operationStatus({
      operation_id: "4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b",
      state: "queued",
    }),
    false,
    "operation_status with a platform-only state",
  );
  assert.equal(
    protocolError({ code: "something_else", message: "x" }),
    false,
    "protocol error with an unknown code",
  );
});

test("operation and status fixtures validate against the v1 robots schemas", async () => {
  const status = await validatorFor("schemas/robots/v1/robot-status-response.schema.json");
  const operation = await validatorFor("schemas/robots/v1/operation.schema.json");
  const createOperation = await validatorFor("schemas/robots/v1/create-operation-request.schema.json");

  assert.ok(status(await loadJson("fixtures/robots/v1/robot-status-online.json")), "status online fixture");
  assert.ok(status(await loadJson("fixtures/robots/v1/robot-status-offline.json")), "status offline fixture");
  assert.ok(operation(await loadJson("fixtures/robots/v1/operation-queued.json")), "operation queued fixture");
  assert.ok(operation(await loadJson("fixtures/robots/v1/operation-succeeded.json")), "operation succeeded fixture");
  assert.ok(createOperation({ kind: "diagnostic.noop", payload_version: 1, payload: {} }), "create operation request");
  assert.ok(createOperation({ kind: "diagnostic.execution.start", payload_version: 1, payload: { fixture: "hello" } }));
  assert.ok(createOperation({ kind: "diagnostic.execution.stop", payload_version: 1, payload: { execution_id: "4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b" } }));
  assert.ok(createOperation({ kind: "diagnostic.execution.inspect", payload_version: 1, payload: { execution_id: "4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b", stream: "stdout", offset: 0, limit: 16384 } }));
  assert.equal(createOperation({ kind: "diagnostic.execution.start", payload_version: 1, payload: { fixture: "unknown" } }), false);
  assert.equal(createOperation({ kind: "diagnostic.execution.inspect", payload_version: 1, payload: { execution_id: "4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b", stream: "stdout", offset: 0, limit: 16385 } }), false);
  assert.equal(createOperation({ kind: "diagnostic.execution.inspect", payload_version: 1, payload: { execution_id: "4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b", stream: "stdout", offset: 65537, limit: 1 } }), false);

  assert.equal(
    createOperation({ kind: "mission.run", payload_version: 1, payload: {} }),
    false,
    "create request with an unsupported kind",
  );
  assert.equal(
    operation({
      id: "4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b",
      robot_id: "7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e",
      kind: "diagnostic.noop",
      payload_version: 1,
      payload: {},
      state: "preparing",
      created_at: "2026-11-10T09:00:05Z",
      updated_at: "2026-11-10T09:00:05Z",
      deadline_at: "2026-11-10T09:01:05Z",
      acknowledged_at: null,
      result: null,
    }),
    false,
    "operation with an execution-era state",
  );
  assert.equal(
    status({
      robot_id: "7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e",
      online: "yes",
      last_seen_at: "2026-11-10T09:00:14Z",
      daemon_version: null,
      protocol_version: null,
    }),
    false,
    "status with a non-boolean online",
  );
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


test("runner v1 fixtures validate and output limits hold", async () => {
  for (const [name, schema] of [["start", "start-execution-request"], ["execution", "execution"], ["output", "output"]]) {
    const validate = await validatorFor(`schemas/runner/v1/${schema}.schema.json`);
    assert.ok(validate(await loadJson(`fixtures/runner/v1/${name}.json`)), name);
  }
  const error = await validatorFor("schemas/errors/v1/error.schema.json");
  assert.ok(error(await loadJson("fixtures/runner/v1/error-execution-conflict.json")));
  const start = await validatorFor("schemas/runner/v1/start-execution-request.schema.json");
  assert.equal(start({execution_id: "invalid", revision_digest: "bad", code_ref: "x", entrypoint: "x"}), false);
});


test("device pairing fixtures and exclusive claim targets", async () => {
  for (const [name, schema] of [["create", "create-request"], ["claim", "claim-request"], ["pending", "poll-response"], ["claimed", "poll-response"]]) {
    const validate = await validatorFor(`schemas/pairing/v1/${schema}.schema.json`);
    assert.ok(validate(await loadJson(`fixtures/pairing/v1/${name}.json`)), name);
  }
  const claim = await validatorFor("schemas/pairing/v1/claim-request.schema.json");
  assert.equal(claim({code:"ABCD-EFGH-IJKL-MNOP-QRST-UVWX-YZ", name:"x", robot_id:"4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b"}), false);
});
