import Ajv2020 from "ajv/dist/2020.js";
import { readFile } from "node:fs/promises";
import { createHash } from "node:crypto";

const ajv = new Ajv2020({ strict: true, allErrors: true, strictRequired: true });
const schemas = new Map(await Promise.all([1, 2].map(async version => [version,
  ajv.compile(JSON.parse(await readFile(new URL(`../../schemas/apps/v${version}/manifest.schema.json`, import.meta.url))))])));
const fail = message => { throw new Error(message); };
function checkUnicode(value) {
  if (typeof value === "string" && !value.isWellFormed()) fail("invalid_unicode");
  if (value && typeof value === "object") {
    for (const [key, item] of Object.entries(value)) {
      checkUnicode(key);
      checkUnicode(item);
    }
  }
}

// Executable contract oracle for future independent API/CLI/Daemon readers.
// These are semantic checks in addition to the public JSON Schema.
export function validateManifest(manifest) {
  checkUnicode(manifest);
  if (Buffer.byteLength(JSON.stringify(manifest), "utf8") > 65536) fail("manifest_too_large");
  const structural = schemas.get(manifest.schema_version);
  if (!structural) fail("invalid_manifest: unsupported_manifest_version");
  if (!structural(manifest)) fail(`invalid_manifest: ${ajv.errorsText(structural.errors)}`);
  const targets = new Set();
  for (const target of manifest.targets) {
    const platform = `${target.os}/${target.architecture}`;
    if (targets.has(platform)) fail("ambiguous_target");
    targets.add(platform);
  }
  const required = new Set(manifest.capabilities.required.map(canonicalJson));
  if (manifest.capabilities.optional.some(p => required.has(canonicalJson(p)))) fail("overlapping_capability");
  const config = manifest.configuration.schema;
  for (const name of config.required) {
    if (!Object.hasOwn(config.properties, name)) fail("unknown_required_config");
  }
  for (const rule of Object.values(config.properties)) {
    if (rule.minimum !== undefined && rule.maximum !== undefined && rule.minimum > rule.maximum) fail("invalid_config_bounds");
    if (rule.minLength !== undefined && rule.minLength > (rule.maxLength ?? 4096)) fail("invalid_config_bounds");
    if (rule.enum) {
      const { enum: choices, ...bounds } = rule;
      const check = ajv.compile(bounds);
      if (choices.some(value => !check(value))) fail("invalid_config_enum");
    }
  }
  validateConfiguration(manifest, manifest.configuration.defaults);
  if (manifest.schema_version === 2) {
    const environment = manifest.configuration.environment ?? {};
    if (Object.keys(environment).length > 32) fail("invalid_environment_mapping");
    const declared = new Set(Object.keys(config.properties));
    for (const [name, key] of Object.entries(environment)) {
      if (!/^[A-Z_][A-Z0-9_]*$/.test(name) ||
          name.startsWith("RINGFOUR_") || name.startsWith("LD_") || name.startsWith("DYLD_") ||
          name.startsWith("PYTHON") || ["PATH", "IFS", "ENV", "BASH_ENV", "BASHOPTS", "SHELLOPTS",
            "NODE_OPTIONS", "NODE_PATH", "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "RUBYOPT", "RUBYLIB",
            "PERL5OPT", "PERL5LIB"].includes(name) || !declared.has(key)) {
        fail("invalid_environment_mapping");
      }
    }
    if (manifest.runtime_settings) {
      const settings = manifest.runtime_settings;
      if (!settings.schema || !settings.defaults) fail("invalid_runtime_settings");
      const settingsManifest = { configuration: settings };
      validateConfiguration(settingsManifest, settings.defaults);
    }
  }
  return manifest;
}

export function validateConfiguration(manifest, values) {
  checkUnicode(values);
  if (Buffer.byteLength(JSON.stringify(values), "utf8") > 16384) fail("config_too_large");
  // Add universal limits, including when the author omitted an optional bound.
  const schema = structuredClone(manifest.schema?.type === "object"
    ? manifest.schema
    : manifest.configuration.schema);
  for (const rule of Object.values(schema.properties)) {
    if (rule.type === "string") rule.maxLength ??= 4096;
    if (rule.type === "integer") {
      rule.minimum ??= -Number.MAX_SAFE_INTEGER;
      rule.maximum ??= Number.MAX_SAFE_INTEGER;
    }
    if (rule.type === "number") {
      rule.minimum ??= -Number.MAX_SAFE_INTEGER;
      rule.maximum ??= Number.MAX_SAFE_INTEGER;
    }
  }
  const validate = ajv.compile(schema);
  if (!validate(values)) fail(`invalid_config: ${ajv.errorsText(validate.errors)}`);
  const environment = manifest.configuration?.environment ?? {};
  let environmentBytes = 0;
  for (const [name, key] of Object.entries(environment)) {
    if (!(key in values)) continue;
    const value = values[key];
    if (typeof value === "string" && value.includes("\0")) fail("invalid_environment_value");
    if (!["string", "boolean", "number"].includes(typeof value)) fail("invalid_environment_value");
    const rendered = String(value);
    environmentBytes += Buffer.byteLength(name, "utf8") + Buffer.byteLength(rendered, "utf8") + 1;
  }
  if (environmentBytes > 16384) fail("environment_too_large");
  return values;
}

// Manifest keys are ASCII. Integers are safe JSON integers; arrays retain order.
export function canonicalJson(value) {
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(",")}]`;
  if (value !== null && typeof value === "object") {
    return `{${Object.keys(value).sort().map(key => `${JSON.stringify(key)}:${canonicalJson(value[key])}`).join(",")}}`;
  }
  return JSON.stringify(value);
}
export function manifestDigest(manifest) {
  validateManifest(manifest);
  return `sha256:${createHash("sha256").update(canonicalJson(manifest), "utf8").digest("hex")}`;
}
