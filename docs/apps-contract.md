# App manifest v1 — new Ringfour design

Status: AP-01 contract acceptance complete. AP-05 implements manifest semantic
validation and registry-verified publication; installation/execution remain later
slices. See the active plan for deployed acceptance. The normative input is
[app-pivot.md](../../app-pivot.md), sections 1–9. The standalone Robot Check JSON
fixture is illustrative release metadata, not a published image or another
example repository.

## Authority

An App is stable within its authenticated publisher workspace, keyed by
`app_key`. Platform assigns its UUID and derives publisher authority from current
membership; a manifest never asserts a publisher. `release` identifies an
immutable release under that App (three decimal version components). Publishing
the same key/release with different canonical content must conflict. API IDs,
imports and sharing rights are outside the manifest and defined by the API contract.

Infrastructure manages Daemon and Agent. Only the authorized Daemon controls its
robot's workloads through an operator-provided engine socket. Engine authority
is trusted, including the administrators of a shared engine. Workloads and
server containers never receive that socket. Neither manifest nor configuration
can supply engine parameters, privileges, host mounts, devices, networks or
command/entrypoint overrides. Owners choose supported deployment policy outside
the manifest. Agent retains ROS and safety ownership.

## Structural and semantic validation

The structural contract is
[manifest.schema.json](../packages/contracts/schemas/apps/v1/manifest.schema.json).
Consumers must also enforce these rules, in this order:

1. Accept UTF-8 JSON objects, at most 65,536 input bytes. Reject duplicate object
   members before decoding and reject unpaired Unicode surrogates. Reject unknown versions/fields at every defined
   object level. Do not fetch any remote schema or reference. This slice's
   executable oracle accepts decoded objects; raw parser rejection remains an
   obligation of future readers.
2. Apply the structural schema. Strings are case-sensitive; do not trim, coerce
   values, add defaults or strip unknown fields. Display text is plain text.
3. Require unique `(os, architecture)` targets, even if their digests differ.
   A single Linux amd64 or arm64 target is sufficient. Images require an
   explicit registry host, repository and lower-case SHA-256 digest, without a
   tag, credentials, scheme or query. Registry resolution must verify that the
   persisted digest is a concrete matching image, not a multi-platform index.
   Tooling may resolve an authoring index before publishing this manifest;
   AP-05 verifies the concrete manifest and configuration; authoring index resolution belongs to AP-06.
4. Reject the same capability predicate in required and optional lists.
5. Validate configuration schema semantics, then validate defaults as a complete
   configuration. Every required key must be declared. Bounds must be ordered;
   every enum member must satisfy its property's type and bounds.

Targets carry up to 16 `native_requirements` identifiers. These are declared
host prerequisites, never permission to mount host resources or install drivers.
The initial Robot Check declares none. Later device matching must refuse an
unverifiable hard prerequisite rather than treating a declaration as evidence.

The v1 capability vocabulary is `mobility`, `cameras`, `lidar`, `imu`, `slam`,
`teleop`, `watches`, `places`, `audit`, `status` and `groups`. A simple predicate
is `{ "capability": "imu" }`. The only instance constraint is
`{ "capability": "groups", "instance_kind": "arm" }`, requiring `groups`
and a nonempty Agent `instances.arms` inventory. Generic groups alone does not
establish an arm. Unknown strings such as `arm.motion` fail validation. These
predicates describe inventory, not permissions, method availability or fresh
sensor data. Missing optional predicates explain limitations without blocking;
missing required inventory and unavailable current inventory are distinct
future device outcomes. AP-03 adds current observed matching, not this slice.

## Configuration and canonical content

Configuration is a closed flat object with at most 32 ASCII snake_case keys.
Supported types are `string`, `integer`, `number` and `boolean`; strings may
specify `minLength`, `maxLength` and `enum`; numeric types may specify
`minimum`, `maximum` and `enum`. A `number` is finite IEEE-754 binary64 within
±9,007,199,254,740,991. NaN, infinity, overflow, underflow and boolean/string
coercion are rejected. No nesting, arrays, null, regex, formats, references, combinators,
secret injection or arbitrary JSON Schema keywords. This intentionally small
subset is sufficient for Robot Check's `sample_count`. All integers, bounds and
values stay within ±9,007,199,254,740,991; strings contain at most 4,096 Unicode
code points. Enum lists have 1–64 distinct values. Configurations, including
defaults, have a 16,384-byte UTF-8 serialized limit. Omitted bounds still obey
these universal limits.

Defaults must satisfy every required field and constraint. A configuration
request is a complete replacement object, never a merge. Clients can explicitly
start from defaults; validators do not insert them. Revisions bind the schema
and defaults immutably. Runtime input is data for the App, never engine settings.

After validation, recursively sort object keys in ASCII order, retain array
order, and serialize compact JSON with no whitespace. Numbers use
ECMAScript-compatible shortest round-trip decimal notation, so each consumer
reconstructs the same binary64 value; integral-valued numbers use integer
notation and negative zero is zero. Strings use JSON escaping
(quote, backslash and control characters), retain other Unicode characters and
apply no Unicode normalization. Hash the UTF-8 bytes using SHA-256, represented as
`sha256:<64 lower-case hex>`. Metadata changes therefore change the manifest
digest. Raw parser and cross-language readers must preserve these rules; the
[canonical fixture](../packages/contracts/fixtures/apps/v1/canonical.json)
provides exact serialized bytes and expected digest. The
[decoded-object oracle](../packages/contracts/test/support/app-manifest.mjs)
is test support, not a shared application implementation.

The supplementary Unicode/line-separator fixture is
[canonical-unicode.json](../packages/contracts/fixtures/apps/v1/canonical-unicode.json);
API, CLI and Daemon readers use its exact UTF-8 canonical bytes/digest.

[API/runtime contracts](apps-api-contract.md) now define actor and observation
timestamps, idempotency, grants, staging/application transitions, stop
preconditions and runtime run attribution. [Device negotiation/inventory](apps-device-contract.md) and cross-language
fixture readers complete the AP-01 contract gate. No existing v1
device/runtime response schema or active typed consumer changes here.
