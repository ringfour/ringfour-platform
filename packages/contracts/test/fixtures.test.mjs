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

test("mission manifest fixture rejects traversal and unknown fields", async () => {
  const validate = await validatorFor("schemas/missions/v1/manifest.schema.json");
  const manifest = await loadJson("fixtures/missions/v1/manifest.json");
  assert.ok(validate(manifest));
  assert.equal(validate({ ...manifest, entrypoint: "../secret.py" }), false);
  assert.equal(validate({ ...manifest, schema_version: 2 }), false);
  assert.equal(validate({ ...manifest, source_digest: "bad" }), false);
  assert.equal(validate({ ...manifest, undocumented: true }), false);
});

test("local CLI mission context fixture rejects unknown fields", async () => {
  const validate = await validatorFor("schemas/missions/v1/cli-context.schema.json");
  const context = await loadJson("fixtures/missions/v1/cli-context.json");
  assert.ok(validate(context), JSON.stringify(validate.errors));
  assert.equal(validate({ ...context, version: 2 }), false);
  assert.equal(validate({ ...context, targets: [{ ...context.targets[0], token: "secret" }] }), false);
});

test("mission deployment and action responses validate", async () => {
  const deployment = await validatorFor("schemas/missions/v1/deployment-accepted.schema.json");
  const action = await validatorFor("schemas/missions/v1/action-accepted.schema.json");
  assert.ok(deployment(await loadJson("fixtures/missions/v1/deployment-accepted.json")));
  assert.ok(action(await loadJson("fixtures/missions/v1/action-accepted.json")));
  const state = await validatorFor("schemas/missions/v1/deployment.schema.json");
  assert.ok(state(await loadJson("fixtures/missions/v1/deployment.json")));
  const prepare = await validatorFor("schemas/missions/v1/runner-prepare.schema.json");
  assert.ok(prepare(await loadJson("fixtures/missions/v1/runner-prepare.json")));
  const prepared = await validatorFor("schemas/missions/v1/runner-prepared.schema.json");
  assert.ok(prepared(await loadJson("fixtures/missions/v1/runner-prepared.json")));
});

test("mission pages and device preparation payloads validate", async () => {
  for (const name of ["mission-page", "revision-page", "device-prepare"]) {
    const validate = await validatorFor(`schemas/missions/v1/${name}.schema.json`);
    const fixture = await loadJson(`fixtures/missions/v1/${name}.json`);
    assert.ok(validate(fixture), JSON.stringify(validate.errors));
    assert.equal(validate({ ...fixture, unexpected: true }), false);
  }
  const error = await validatorFor("schemas/errors/v1/error.schema.json");
  for (const code of ["version_conflict", "execution_conflict", "deployment_in_progress", "robot_unavailable"]) {
    assert.ok(error(await loadJson(`fixtures/missions/v1/error-${code}.json`)));
  }
  const page = await validatorFor("schemas/missions/v1/revision-page.schema.json");
  const fixture = await loadJson("fixtures/missions/v1/revision-page.json");
  assert.equal(page({ ...fixture, items: [{ ...fixture.items[0], manifest: {} }] }), false);
});

test("Runner artifact fixtures enforce bounds", async () => {
  const list = await validatorFor("schemas/runner/v1/artifact-list.schema.json");
  const chunk = await validatorFor("schemas/runner/v1/artifact-chunk.schema.json");
  const listing = await loadJson("fixtures/runner/v1/artifact-list.json");
  const output = await loadJson("fixtures/runner/v1/artifact-chunk.json");
  assert.ok(list(listing));
  assert.ok(chunk(output));
  assert.equal(list({ ...listing, artifacts: Array(17).fill(listing.artifacts[0]) }), false);
  assert.equal(chunk({ ...output, next_offset: 262145 }), false);
});

test("script revision request enforces upload bounds", async () => {
  const validate = await validatorFor("schemas/scripts/v1/create-revision-request.schema.json");
  const request = await loadJson("fixtures/scripts/v1/create-revision.json");
  assert.ok(validate(request));
  assert.equal(validate({ ...request, archive_size: 1048577 }), false);
  assert.equal(validate({ ...request, archive_sha256: "wrong" }), false);
  assert.equal(validate({ ...request, unexpected: true }), false);
});

test("script run fixtures validate and reject extra fields", async () => {
  const request = await validatorFor("schemas/scripts/v1/create-run-request.schema.json");
  const response = await validatorFor("schemas/scripts/v1/create-run-response.schema.json");
  const input = await loadJson("fixtures/scripts/v1/create-run.json");
  assert.ok(request(input));
  assert.ok(response(await loadJson("fixtures/scripts/v1/create-run-response.json")));
  assert.equal(request({ ...input, revision_id: "bad" }), false);
  assert.equal(request({ ...input, extra: true }), false);
});

test("script revision response fixture validates", async () => {
  const revision = await validatorFor("schemas/scripts/v1/revision.schema.json");
  const body = await loadJson("fixtures/scripts/v1/revision-uploading.json");
  assert.ok(revision(body));
  assert.equal(revision({ ...body, state: "unknown" }), false);
});

test("script execution and device status fixtures validate", async () => {
  const execution = await validatorFor("schemas/scripts/v1/execution.schema.json");
  const status = await validatorFor("schemas/scripts/v1/device-status.schema.json");
  const body = await loadJson("fixtures/scripts/v1/device-status.json");
  assert.ok(execution(await loadJson("fixtures/scripts/v1/execution.json")));
  assert.ok(status(body));
  assert.equal(status({ ...body, state: "unknown" }), false);
});

test("device script command fixtures validate", async () => {
  const start = await validatorFor("schemas/scripts/v1/device-start.schema.json");
  const stop = await validatorFor("schemas/scripts/v1/device-stop.schema.json");
  const body = await loadJson("fixtures/scripts/v1/device-start.json");
  assert.ok(start(body));
  assert.ok(start({ ...body, mission_revision_id: body.revision_id }));
  assert.equal(start({ ...body, mission_revision_id: "bad" }), false);
  assert.ok(stop(await loadJson("fixtures/scripts/v1/device-stop.json")));
  assert.equal(start({ ...body, entrypoint: "../hello.py" }), false);
});

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

test("development session proposal fixtures and source bounds validate", async () => {
  for (const name of ["create-request", "session", "source-request", "accepted", "runner-sync", "runner-restart", "lifecycle-command", "sync-result", "grant-request", "grant-accepted", "device-grant", "local-status", "device-endpoint", "device-source-report", "local-source-state", "runner-restart-result", "restart-request", "device-run-status"]) {
    const validate = await validatorFor(`schemas/dev-sessions/v1/${name}.schema.json`);
    const fixture = await loadJson(`fixtures/dev-sessions/v1/${name}.json`);
    assert.ok(validate(fixture), `${name}: ${JSON.stringify(validate.errors)}`);
    assert.equal(validate({ ...fixture, unexpected: true }), false);
  }
  const source = await validatorFor("schemas/dev-sessions/v1/source-request.schema.json");
  const input = await loadJson("fixtures/dev-sessions/v1/source-request.json");
  assert.equal(source({ ...input, base_generation: -1 }), false);
  assert.equal(source({ ...input, archive_size: 1048577 }), false);
  assert.equal(source({ ...input, archive_base64: "!bad" }), false);
  assert.equal(source({ ...input, manifest: { ...input.manifest, entrypoint: "../main.py" } }), false);
  assert.equal(source({ ...input, manifest: { ...input.manifest, deleted_paths: ["a", "a"] } }), false);
  const grant = await validatorFor("schemas/dev-sessions/v1/grant-accepted.schema.json");
  const grantFixture = await loadJson("fixtures/dev-sessions/v1/grant-accepted.json");
  assert.ok(grant({ ...grantFixture, daemon_url: null }));
  assert.equal(grant({ ...grantFixture, daemon_url: "http://robot:8084" }), false);
  assert.equal(grant({ ...grantFixture, certificate_sha256: "bad" }), false);
  const deviceGrant = await validatorFor("schemas/dev-sessions/v1/device-grant.schema.json");
  const deviceFixture = await loadJson("fixtures/dev-sessions/v1/device-grant.json");
  assert.equal(deviceGrant({ ...deviceFixture, scopes: ["source", "source"] }), false);
  const create = await validatorFor("schemas/dev-sessions/v1/create-request.schema.json");
  assert.equal(create({ ttl_seconds: 299 }), false);
  assert.equal(create({ ttl_seconds: 28801 }), false);
});

test("proposed Logbook event and batch fixtures enforce the allowlisted payload", async () => {
  await validatorFor("schemas/logbook/v1/agent-motion-event.schema.json");
  const event = await validatorFor("schemas/logbook/v1/device-event.schema.json");
  const batch = await validatorFor("schemas/logbook/v1/device-batch.schema.json");
  const checkpoint = await validatorFor("schemas/logbook/v1/device-checkpoint.schema.json");
  const fixture = await loadJson("fixtures/logbook/v1/device-event.json");
  assert.ok(event(fixture), JSON.stringify(event.errors));
  assert.ok(batch(await loadJson("fixtures/logbook/v1/device-batch.json")), JSON.stringify(batch.errors));
  assert.ok(checkpoint(await loadJson("fixtures/logbook/v1/device-checkpoint.json")));
  assert.equal(event({ ...fixture, local_sequence: 0 }), false);
  assert.equal(event({ ...fixture, schema_version: 2 }), false);
  assert.equal(event({ ...fixture, payload: { state: "running", output: "secret" } }), false);
  assert.equal(event({ ...fixture, actor_user_id: fixture.robot_id }), false);
  assert.equal(batch({ events: [] }), false);
  assert.equal(batch({ events: Array(101).fill(fixture) }), false);
  assert.equal(checkpoint({ acknowledged_sequence: -1 }), false);
  const motion = await loadJson("fixtures/logbook/v1/device-agent-motion.json");
  assert.ok(event(motion), JSON.stringify(event.errors));
  assert.equal(event({ ...motion, operation_id: fixture.operation_id }), false);
  assert.equal(event({ ...motion, payload: { ...motion.payload, stop_verified: false } }), false);
  const error = await validatorFor("schemas/errors/v1/error.schema.json");
  for (const name of ["sequence-gap", "event-conflict"]) {
    assert.ok(error(await loadJson(`fixtures/logbook/v1/error-${name}.json`)));
  }
});

test("proposed mission Logbook entries preserve author fields and reject forged context", async () => {
  const entry = await validatorFor("schemas/logbook/v1/runtime-entry-request.schema.json");
  const accepted = await validatorFor("schemas/logbook/v1/runtime-entry-accepted.schema.json");
  const input = await loadJson("fixtures/logbook/v1/runtime-entry-request.json");
  assert.ok(entry(input), JSON.stringify(entry.errors));
  assert.ok(accepted(await loadJson("fixtures/logbook/v1/runtime-entry-accepted.json")));
  assert.equal(entry({ ...input, message: "   " }), false);
  assert.equal(entry({ ...input, robot_id: "forged" }), false);
  assert.equal(entry({ ...input, fields: { nested: { value: "not supported" } } }), false);
  assert.equal(entry({ ...input, fields: { samples: [1, 2] } }), false);
  assert.equal(entry({ ...input, fields: Object.fromEntries(Array.from({ length: 17 }, (_, i) => [`field${i}`, i])) }), false);
  assert.equal(entry({ ...input, fields: { "bad-key": "value" } }), false);
  assert.equal(entry({ ...input, level: "debug" }), false);
});

test("mission author device events carry execution and bounded structured fields", async () => {
  const validate = await validatorFor("schemas/logbook/v1/device-event.schema.json");
  const event = await loadJson("fixtures/logbook/v1/device-author-entry.json");
  assert.ok(validate(event), JSON.stringify(validate.errors));
  const { execution_id, ...missing } = event;
  assert.equal(validate(missing), false);
  assert.equal(validate({ ...event, payload: { ...event.payload, fields: { nested: [1] } } }), false);
  assert.equal(validate({ ...event, actor_user_id: execution_id }), false);
});

test("proposed organization Logbook history page includes attribution", async () => {
  const validate = await validatorFor("schemas/logbook/v1/history-page.schema.json");
  const fixture = await loadJson("fixtures/logbook/v1/history-page.json");
  assert.ok(validate(fixture), JSON.stringify(validate.errors));
  assert.equal(validate({ ...fixture, items: [{ ...fixture.items[0], actor_user_id: "invalid" }] }), false);
  assert.equal(validate({ ...fixture, items: [{ ...fixture.items[0], secret: "not allowed" }] }), false);
});

test("proposed Agent motion records cannot assert an unverified stop", async () => {
  const validate = await validatorFor("schemas/logbook/v1/agent-motion-event.schema.json");
  const input = await loadJson("fixtures/logbook/v1/agent-motion-event.json");
  assert.ok(validate(input), JSON.stringify(validate.errors));
  assert.equal(validate({ ...input, stop_verified: false }), false);
  assert.equal(validate({ ...input, event_type: "motion.accepted", stop_verified: true }), false);
  assert.equal(validate({ ...input, velocity: 0.5 }), false);
  assert.equal(validate({ ...input, owner: "bad\0owner" }), false);
  assert.ok(validate({ ...input, event_type: "motion.guard_tripped", stop_verified: null, operation_id: null, owner: null }));
});

test("local Agent handoff fixtures are scoped and bounded", async () => {
  await validatorFor("schemas/logbook/v1/agent-motion-event.schema.json");
  const request = await validatorFor("schemas/logbook/v1/agent-handoff-request.schema.json");
  const response = await validatorFor("schemas/logbook/v1/agent-handoff-response.schema.json");
  const read = await loadJson("fixtures/logbook/v1/agent-handoff-read.json");
  const ack = await loadJson("fixtures/logbook/v1/agent-handoff-ack.json");
  const page = await loadJson("fixtures/logbook/v1/agent-handoff-page.json");
  assert.ok(request(read), JSON.stringify(request.errors));
  assert.ok(request(ack), JSON.stringify(request.errors));
  assert.ok(response(page), JSON.stringify(response.errors));
  assert.ok(response({ok: true, result: null}));
  assert.ok(response({ok: false, error: "history_gap"}));
  assert.equal(request({...read, method: "move"}), false);
  assert.equal(request({...read, limit: 101}), false);
  assert.equal(request({...ack, sequence: -1}), false);
  assert.equal(response({ok: false, error: "unexpected"}), false);
});
