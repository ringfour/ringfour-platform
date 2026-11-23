import Ajv2020 from "ajv/dist/2020.js";
import { readFile } from "node:fs/promises";
import { addContractFormats } from "./formats.mjs";
import { canonicalJson } from "./app-manifest.mjs";

const base = "https://contracts.ringfour.dev/";
const ajv = addContractFormats(new Ajv2020({strict: true, allErrors: true}));
for (const path of ["apps/v1/manifest.schema.json", "errors/v2/app-error.schema.json", "apps/v1/api.schema.json", "apps/v1/requests.schema.json", "interactions/v2/app-interaction.schema.json", "artifacts/v2/app-artifact.schema.json", "logbook/v3/app-history-page.schema.json", "device/apps/v1/protocol.schema.json", "device/v1/command.schema.json"]) {
  ajv.addSchema(JSON.parse(await readFile(new URL(`../../schemas/${path}`, import.meta.url))));
}
const fail = code => { throw new Error(code); };
// Avoid lexicographic fractional-second ordering or millisecond truncation.
const micros = timestamp => BigInt(Date.parse(timestamp.slice(0, 19) + "Z")) * 1000n
  + BigInt((timestamp.match(/\.(\d+)Z$/)?.[1] ?? "").padEnd(6, "0"));
export function validateResource(path, value) {
  const validate = ajv.getSchema(base + path);
  if (!validate) fail(`unknown_schema: ${path}`);
  if (!validate(value)) fail(`invalid_resource: ${ajv.errorsText(validate.errors)}`);
  return value;
}
const api = (name, value) => validateResource(`apps/v1/api.schema.json#/$defs/${name}`, value);
function immutable(before, after, keys) {
  for (const key of keys) {
    if (canonicalJson(before[key]) !== canonicalJson(after[key])) fail(`immutable_${key}`);
  }
}
// State and cross-record invariants are separate from structural JSON Schema.
export function validateRunTransition(before, after) {
  api("run", before); api("run", after);
  immutable(before, after, ["id", "organization_id", "robot_id", "installation_id", "start_operation_id", "snapshot", "requested_by", "requested_at"]);
  const graph = {accepted: ["accepted", "running", "succeeded", "failed", "stopped", "interrupted"], running: ["running", "succeeded", "failed", "stopped", "interrupted"]};
  if (!(graph[before.state] ?? [before.state]).includes(after.state)) fail("invalid_run_transition");
  if (!graph[before.state]) immutable(before, after, Object.keys(before));
  if (before.started_at !== null && before.started_at !== after.started_at) fail("immutable_started_at");
  return after;
}
export function validateOperationTransition(before, after) {
  api("operation", before); api("operation", after);
  immutable(before, after, ["id", "organization_id", "robot_id", "installation_id", "run_id", "kind", "requested_by", "requested_at", "deadline_at"]);
  const graph = {queued: ["queued", "accepted", "running", "succeeded", "failed", "cancelled"], accepted: ["accepted", "running", "succeeded", "failed", "cancelled"], running: ["running", "succeeded", "failed", "cancelled"]};
  if (!(graph[before.state] ?? [before.state]).includes(after.state)) fail("invalid_operation_transition");
  if (!graph[before.state]) immutable(before, after, Object.keys(before));
  if (before.acknowledged_at !== null && before.acknowledged_at !== after.acknowledged_at) fail("immutable_acknowledged_at");
  const phases = before.kind === "start" ? ["queued", "accepted", "checking", "starting", "observed_running"]
    : before.kind === "stop" ? ["queued", "accepted", "stopping", "stopped"]
    : ["queued", "accepted", "checking", "staging", "staged", "applied"];
  if (phases.includes(after.phase) && phases.indexOf(after.phase) < phases.indexOf(before.phase)) fail("invalid_operation_phase");
  return after;
}
export function validateImportProvenance(imported, revision, share) {
  api("import", imported); api("revision", revision);
  if (imported.app_id !== revision.app_id || imported.revision_id !== revision.id || imported.publisher_organization_id !== revision.publisher_organization_id) fail("invalid_import_provenance");
  if (imported.source.kind === "direct_share") {
    api("share", share);
    if (share.id !== imported.source.share_id || share.app_id !== imported.app_id || share.revision_id !== imported.revision_id || share.publisher_organization_id !== imported.publisher_organization_id || share.destination_organization_id !== imported.organization_id) fail("invalid_share_binding");
    if (micros(imported.imported_at) < micros(share.granted_at) || micros(imported.imported_at) >= micros(share.expires_at) || (share.revoked_at !== null && micros(imported.imported_at) >= micros(share.revoked_at))) fail("invalid_share_time");
  } else if (revision.visibility !== "listed") fail("share_required");
  return imported;
}
export function validateStop(request, installation, run) {
  validateResource("apps/v1/requests.schema.json#/$defs/stop", request);
  api("installation", installation); api("run", run);
  if (run.installation_id !== installation.id || run.robot_id !== installation.robot_id || run.organization_id !== installation.organization_id || run.snapshot.app_id !== installation.app_id) fail("wrong_installation_run");
  if (request.expected_run_id !== run.id) fail("active_run_changed");
  return request;
}
export function validateInstallationTransition(before, after, operation, activeRun = null) {
  api("installation", before); api("installation", after); api("operation", operation);
  immutable(before, after, ["id", "organization_id", "robot_id", "app_id", "created_by", "created_at"]);
  if (operation.installation_id !== after.id || operation.robot_id !== after.robot_id || operation.organization_id !== after.organization_id) fail("wrong_installation_operation");
  if (canonicalJson(before.installed) !== canonicalJson(after.installed)) {
    if (activeRun !== null) {
      api("run", activeRun);
      if (activeRun.robot_id !== after.robot_id || activeRun.organization_id !== after.organization_id) fail("wrong_robot_run");
      if (["accepted", "running"].includes(activeRun.state)) fail("execution_conflict");
    }
    if (operation.state !== "succeeded" || operation.phase !== "applied" || after.installed?.applied_operation_id !== operation.id) fail("application_not_observed");
    if (!["install", "configure", "update", "rollback"].includes(operation.kind)) fail("invalid_apply_operation");
    if (after.candidate !== null) fail("applied_candidate_not_cleared");
  }
  return after;
}

export function validateOutputPage(page) {
  api("output_page", page);
  const bytes = Buffer.from(page.data_base64, "base64");
  if (bytes.length > 16384 || page.offset > 65536 || page.next_offset > 65536
      || page.next_offset !== page.offset + bytes.length) fail("invalid_output_range");
  return page;
}
