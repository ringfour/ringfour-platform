import { test } from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import Ajv2020 from "ajv/dist/2020.js";
import { addContractFormats } from "./support/formats.mjs";

const root = new URL("../", import.meta.url);

async function loadJson(relativePath) {
  return JSON.parse(await readFile(new URL(relativePath, root), "utf8"));
}

const ajv = addContractFormats(new Ajv2020({ allErrors: true, strict: true }));
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

test("robot appearance supports built-in artwork and bounded raster images", async () => {
  const validate = await validatorFor("schemas/robots/v1/update-appearance-request.schema.json");
  for (const appearance of [null, "bot", "base101", "rover", "arm", "drone", "data:image/png;base64,iVBORw0KGgo="]) {
    assert.ok(validate({ appearance }));
  }
  for (const appearance of ["", "unknown", "https://example.com/robot.png", "data:image/svg+xml;base64,PHN2Zy8+", "x".repeat(350001)]) {
    assert.equal(validate({ appearance }), false);
  }
  assert.equal(validate({}), false);
  assert.equal(validate({ appearance: "bot", profile_ref: "changed" }), false);
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
    error: await validatorFor("schemas/device/v1/protocol-error.schema.json"),
  };

  for (const name of [
    "hello",
    "welcome",
    "heartbeat",
    "command",
    "ack",
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
    protocolError({ code: "something_else", message: "x" }),
    false,
    "protocol error with an unknown code",
  );
});

test("robot presence fixtures validate", async () => {
  const status = await validatorFor("schemas/robots/v1/robot-status-response.schema.json");
  assert.ok(status(await loadJson("fixtures/robots/v1/robot-status-online.json")));
  assert.ok(status(await loadJson("fixtures/robots/v1/robot-status-offline.json")));
  assert.equal(status({ robot_id: "7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e", online: "yes", last_seen_at: null, daemon_version: null, protocol_version: null }), false);
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


test("device pairing fixtures and exclusive claim targets", async () => {
  for (const [name, schema] of [["create", "create-request"], ["claim", "claim-request"], ["pending", "poll-response"], ["claimed", "poll-response"]]) {
    const validate = await validatorFor(`schemas/pairing/v1/${schema}.schema.json`);
    assert.ok(validate(await loadJson(`fixtures/pairing/v1/${name}.json`)), name);
  }
  const claim = await validatorFor("schemas/pairing/v1/claim-request.schema.json");
  assert.equal(claim({code:"ABCD-EFGH-IJKL-MNOP-QRST-UVWX-YZ", name:"x", robot_id:"4e5f6a7b-8c9d-4e0f-9a1b-2c3d4e5f6a7b"}), false);
});

test("proposed Logbook event and batch fixtures enforce the allowlisted payload", async () => {
  await validatorFor("schemas/logbook/v1/agent-motion-event.schema.json");
  await validatorFor("schemas/logbook/v2/device-event.schema.json");
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

test("live artifact publication and Logbook evidence are versioned", async () => {
  const publish = await validatorFor("schemas/artifacts/v1/runtime-publish-request.schema.json");
  const artifact = await validatorFor("schemas/artifacts/v1/artifact.schema.json");
  const entry = await validatorFor("schemas/logbook/v2/runtime-entry-request.schema.json");
  const id = "123e4567-e89b-12d3-a456-426614174000";
  assert.ok(publish({ artifact_id: id, path: "photo.png", content_type: "image/png" }));
  assert.ok(artifact({ artifact_id: id, path: "photo.png", size: 3, sha256: "0".repeat(64), content_type: "image/png", state: "available" }));
  assert.ok(entry({ entry_id: id, level: "info", message: "Photo captured", fields: {}, evidence: [id] }));
  assert.equal(publish({ artifact_id: id, path: "../photo.png", content_type: "image/png" }), false);
  assert.equal(entry({ entry_id: id, level: "info", message: "Photo captured", fields: {}, evidence: [id, id] }), false);
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

test("human interactions wait forever by default and require paired timeout fields", async () => {
  const create = await validatorFor("schemas/interactions/v1/create-request.schema.json");
  const interaction = await validatorFor("schemas/interactions/v1/interaction.schema.json");
  const sync = await validatorFor("schemas/interactions/v1/device-sync-request.schema.json");
  const request = await loadJson("fixtures/interactions/v1/create-request-no-timeout.json");
  const pending = await loadJson("fixtures/interactions/v1/pending-no-timeout.json");
  const syncRequest = await loadJson("fixtures/interactions/v1/device-sync-no-timeout.json");
  assert.ok(create(request), JSON.stringify(create.errors));
  assert.ok(interaction(pending), JSON.stringify(interaction.errors));
  assert.ok(sync(syncRequest), JSON.stringify(sync.errors));
  assert.equal(create({ ...request, timeout_outcome: "abort" }), false);
  assert.equal(create({ ...request, deadline: "2026-11-17T15:00:00Z" }), false);
  assert.ok(create({ ...request, deadline: "2026-11-17T15:00:00Z", timeout_outcome: "abort" }));
});

test("identity formats validate email and absolute URI", async () => {
  const validate = await validatorFor("schemas/identity/v1/user.schema.json");
  const { user } = await loadJson("fixtures/identity/v1/me.json");
  assert.ok(validate({ ...user, email: "robot.owner+test@example.com", profile_picture_url: "https://example.com/photo.png" }));
  for (const email of ["missing-at", "a@@example.com", "a..b@example.com", ".a@example.com", "a@-bad.com", "a@", "a b@example.com"]) {
    assert.equal(validate({ ...user, email }), false, email);
  }
  assert.equal(validate({ ...user, profile_picture_url: "/relative.png" }), false);
  assert.equal(validate({ ...user, profile_picture_url: "https://example.com/%XX" }), false);
});
