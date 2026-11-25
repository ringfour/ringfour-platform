import {test} from "node:test";
import assert from "node:assert/strict";
import {readFile} from "node:fs/promises";
import {validateResource, validateRunTransition, validateOperationTransition, validateImportProvenance, validateStop, validateInstallationTransition, validateOutputPage} from "./support/apps-api.mjs";

const load = async name => JSON.parse(await readFile(new URL(`../fixtures/apps/v1/${name}.json`, import.meta.url)));
const examples = await load("api");
const example = name => structuredClone(examples.find(f => f.name === name).value);
const requests = await load("requests");
const request = name => structuredClone(requests.find(f => f.name === name).value);
const at = "2026-11-23T10:00:03Z";

test("App API and runtime positive fixtures validate independently of live services", async t => {
  for (const fixture of [...examples, ...requests, ...await load("errors")]) {
    await t.test(fixture.name, () => assert.ok(validateResource(fixture.schema, fixture.value)));
  }
});

test("App API rejected fixtures keep acknowledgement, identity and observation distinct", async t => {
  for (const fixture of await load("api-rejected")) {
    await t.test(fixture.name, () => assert.throws(() => validateResource(fixture.schema, fixture.value), /invalid_resource/));
  }
});

test("AP17 settings and cross-run artifact contracts retain exact identities and bounds", () => {
  const uuid = "10000000-0000-4000-8000-000000000001";
  const revision = {id: uuid, installation_id: uuid, revision_id: uuid, values: {interval_ms: 250}, created_by: uuid, created_at: at};
  const application = {application_id: uuid, run_id: uuid, settings_revision_id: uuid, values: {interval_ms: 250}, state: "pending", delivery_state: "queued", initial: true, requested_at: at, reason: null};
  const state = {installation_id: uuid, support: "supported", runtime_settings: {schema: {}, defaults: {}}, selected_for_next_start: revision, active_run_id: uuid, last_applied: null, pending_application: application};
  assert.ok(validateResource("apps/v2/settings.schema.json#/$defs/settings_state", state));
  assert.ok(validateResource("apps/v2/settings.schema.json#/$defs/settings_write", {values: {interval_ms: 500}, expected_selected_revision_id: uuid, expected_applied_revision_id: null, expected_run_id: uuid}));
  assert.ok(validateResource("apps/v2/settings.schema.json#/$defs/runtime_pending", {run_id: uuid, application}));
  assert.ok(validateResource("apps/v2/settings.schema.json#/$defs/runtime_result", {outcome: "rejected", reason: "outside app limits"}));
  const page = {items: [{artifact_id: uuid, run_id: uuid, installation_id: uuid, robot_id: uuid, path: "reports/summary.json", size: 10, sha256: "a".repeat(64), content_type: "application/octet-stream", published_at: at}], next_cursor: null};
  assert.ok(validateResource("artifacts/v3/app-artifact-page.schema.json", page));
  assert.throws(() => validateResource("apps/v2/settings.schema.json#/$defs/runtime_result", {outcome: "applied", reason: "bad\nreason"}), /invalid_resource/);
});

test("direct share provenance binds revision, publisher and destination", () => {
  const imported = example("consumer_import"), revision = example("unlisted_revision"), share = example("direct_share");
  assert.ok(validateImportProvenance(imported, revision, share));
  assert.throws(() => validateImportProvenance({...imported, imported_at: "2026-11-23T10:00:00.000001Z"}, revision, {...share, granted_at: "2026-11-23T10:00:00Z", expires_at: "2026-11-23T10:00:00.000001Z"}), /invalid_share_time/);
  for (const field of ["organization_id", "revision_id", "publisher_organization_id", "app_id"]) {
    assert.throws(() => validateImportProvenance({...imported, [field]: "ffffffff-ffff-4fff-8fff-ffffffffffff"}, revision, share), /invalid_(?:import_provenance|share_binding)/);
  }
  assert.throws(() => validateImportProvenance(imported, revision, {...share, expires_at: share.granted_at}), /invalid_share_time/);
  assert.throws(() => validateImportProvenance(imported, revision, {...share, revoked_at: share.granted_at}), /invalid_share_time/);
  assert.ok(validateImportProvenance(imported, revision, {...share, revoked_at: "2026-11-23T11:00:00Z"}), "later grant revocation preserves existing import");
  assert.throws(() => validateImportProvenance({...imported, source: {kind: "store"}}, revision, null), /share_required/);
  assert.ok(validateImportProvenance({...imported, source: {kind: "store"}}, {...revision, visibility: "listed"}, null));
});

test("install acknowledgement cannot apply a binding and staging preserves installed v1", () => {
  const before = example("not_yet_installed"), installed = example("installed"), ack = example("install_ack");
  assert.equal(ack.observed_at, null);
  assert.equal(ack.run_id, null);
  assert.throws(() => validateInstallationTransition(before, installed, ack), /application_not_observed/);
  const applied = {...ack, state: "succeeded", phase: "applied", observed_at: at, received_at: at, completed_at: at};
  assert.ok(validateInstallationTransition(before, installed, applied));
  const staged = example("staged_update_keeps_v1");
  assert.deepEqual(staged.installed, installed.installed);
  assert.notEqual(staged.candidate.revision_id, staged.installed.revision_id);
  assert.throws(() => validateInstallationTransition(before, installed, applied, example("observed_run")), /execution_conflict/);
});

test("stop is fenced to one installation and concrete run, including delayed retries", () => {
  const installation = example("installed"), run = example("observed_run"), stop = request("stop");
  assert.ok(validateStop(stop, installation, run));
  const laterRun = {...run, id: "ffffffff-ffff-4fff-8fff-ffffffffffff"};
  assert.throws(() => validateStop(stop, installation, laterRun), /active_run_changed/);
  assert.throws(() => validateStop(stop, {...installation, robot_id: laterRun.id}, run), /wrong_installation_run/);
  assert.throws(() => validateStop(stop, {...installation, organization_id: laterRun.id}, run), /wrong_installation_run/);
});

test("run history and actor snapshot are immutable across update and loss", () => {
  const run = example("observed_run"), interrupted = example("lost_container");
  assert.ok(validateRunTransition(run, interrupted));
  const modified = structuredClone(interrupted);
  modified.snapshot.configuration.sample_count = 4;
  assert.throws(() => validateRunTransition(run, modified), /immutable_snapshot/);
  assert.throws(() => validateRunTransition(run, {...interrupted, requested_by: interrupted.robot_id}), /immutable_requested_by/);
  assert.throws(() => validateRunTransition(interrupted, run), /invalid_run_transition/);
  assert.ok(validateRunTransition(interrupted, structuredClone(interrupted)), "identical terminal replay");
  assert.throws(() => validateRunTransition(interrupted, {...interrupted, received_at: "2026-11-23T11:00:00Z"}), /immutable_received_at/);
});

test("operation observations progress forward and completed results are immutable", () => {
  const ack = example("install_ack");
  const staging = {...ack, state: "running", phase: "staging", observed_at: at, received_at: at};
  const staged = {...staging, phase: "staged"};
  const applied = {...staged, state: "succeeded", phase: "applied", completed_at: at};
  assert.ok(validateOperationTransition(ack, staging));
  assert.ok(validateOperationTransition(staging, staged));
  assert.throws(() => validateOperationTransition(staged, staging), /invalid_operation_phase/);
  assert.ok(validateOperationTransition(staged, applied));
  assert.throws(() => validateOperationTransition(applied, ack), /invalid_operation_transition/);
  assert.throws(() => validateOperationTransition(applied, {...applied, completed_at: "2026-11-23T11:00:00Z"}), /immutable_completed_at/);
  const start = {...ack, kind: "start", run_id: example("observed_run").id};
  assert.throws(() => validateResource("apps/v1/api.schema.json#/$defs/operation", {...start, state: "succeeded", phase: "applied", observed_at: at, received_at: at, completed_at: at}), /invalid_resource/);
});

test("App runtime rejects legacy execution attribution and provisional is not applied", () => {
  const interaction = example("pending_app_interaction");
  assert.throws(() => validateResource("interactions/v2/app-interaction.schema.json", {...interaction, execution_id: interaction.run_id}), /invalid_resource/);
  const response = {response_id: interaction.run_id, actor_user_id: interaction.run_id, chosen_value: interaction.choices[0].value, status: "submitted", submitted_at: at, confirmed_at: null};
  const path = "interactions/v2/app-interaction.schema.json";
  assert.ok(validateResource(path, {...interaction, response}));
  assert.throws(() => validateResource(path, {...interaction, response: {...response, status: "applied"}}), /invalid_resource/);
  assert.ok(validateResource(path, {...interaction, state: "answered", outcome: response.chosen_value, response: {...response, status: "applied", confirmed_at: at}}));
});

test("output offsets count bounded decoded bytes and error details explain refusals", async () => {
  const output = example("output_bytes");
  assert.ok(validateOutputPage(output));
  assert.throws(() => validateOutputPage({...output, next_offset: 4}), /invalid_output_range/);
  assert.throws(() => validateOutputPage({...output, offset: 65536, next_offset: 65539}), /invalid_output_range/);
  const failure = (await load("errors")).find(f => f.name === "missing_required");
  assert.throws(() => validateResource(failure.schema, {error: {...failure.value.error, details: {retryable: false}}}), /invalid_resource/);
});
