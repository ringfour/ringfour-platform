# App authoring and publication

`app.json` follows the authoring v1 schema: manifest metadata/capabilities/config,
editable target repositories, explicit Dockerfile and source-file allowlist,
and a checksum-pinned local wheel bundle. It is not publication input.
`app.manifest.json` follows manifest v1 and contains only concrete image digests.
Authoring schema v2 remains additive: v1 files and published releases continue
to validate unchanged. Select `"schema_version": 2` to declare startup
environment mappings or live settings.

Configuration fields may use `string`, `integer`, bounded finite `number`, or
`boolean`. Numbers are IEEE-754 binary64 values with absolute value at most
9,007,199,254,740,991; canonical JSON uses shortest round-trip decimal
notation. NaN, infinities, overflow, underflow, booleans and strings are not
accepted as numbers.

`ringfour-cli scaffold [DIRECTORY]` clones a minimal App with a configurable
greeting, a development image target and a release target. The default directory
is `my-ringfour-app`. Run `ringfour-cli app validate .` from the checkout after
editing `app.json`. See the [CLI guide](../apps/cli/README.md#clone-a-starter-app)
for local and mirrored template repositories.

`app init PATH --app-key KEY --repository REGISTRY/REPO` extracts the one Robot
Check template from a versioned checksum-pinned archive, then applies the selected
identity. The standalone example repository owns the source. The CLI packages a
release pointer, not a divergent template copy. Local archives are transferable;
no sibling checkout or public feed is required to consume them.

`app validate PATH` checks authoring JSON, duplicate keys/Unicode/safe integers,
manifest semantics, configuration schema/defaults, unique targets and safe explicit
source paths. `--manifest FILE` checks a finalized manifest and rejects mutable
tags. No remote schemas are fetched and no defaults silently change input.

`app build PATH --wheels BUNDLE` stages only declared regular files plus verified
wheel inputs, then builds one loaded image per platform using Docker Buildx.
Credentials, context/state files, `.env`, symlinks and engine options never become
implicit build inputs. Images use the Dockerfile's plain entrypoint. Results in
`.ringfour/build.json` bind source fingerprint and local image IDs, not just tags.

These identifiers answer different questions: `source_fingerprint` binds the
authoring document and declared build inputs; Docker `image_id` / published OCI
digest identifies image bytes; API `manifest_digest` identifies the canonical
immutable release manifest, including display metadata. A display-name change
can therefore keep the same image digest while producing a different manifest
digest. Release metadata is immutable: increment `release` in `app.json` before
publishing a changed name or description. The Store name changes when the new
listed release is successfully published. Existing runs retain their frozen
manifest and image references.

`app publish PATH [--org UUID] [--visibility listed|unlisted]` defaults to
private (`unlisted`). `listed` means public App Store discovery and is reserved
for the configured Ringfour publisher organization. The CLI prints the workspace,
App, release and visibility before submission; public publication requires an
interactive confirmation or `--yes`. Automation supplies an explicit visibility
and pass `--yes` when using `--json` to publish a listed release. The server enforces this entitlement independently.
The command uses Docker's normal
registry login, verifies the unchanged build inputs/local IDs, pushes images,
resolves/validates concrete remote digest/platform/config, and freezes the release
before calling the publication API. API credentials stay in the existing Auth0 keyring flow.
Registry success followed by API failure retains the same frozen manifest and
stable request key. Different source content at an already frozen release requires
an explicit version bump. Prior release files are immutable; output may be inspected
after refresh. `--manifest FILE` publishes independently prepared immutable images
without requiring a local build. API metadata is published last.

Commands support `--json` results and bounded structured errors/nonzero exits.
Offline validation requires neither login nor running services. Build/publish never
reload Platform or virtual stacks. Installation and start are separate operations.

The pinned Robot Check v0.2.0 template takes a startup `sample_count` from
1 to 20 (default 20) and a live `sample_interval_ms` from 100 to 5000
(default 1000). It reads IMU samples through the SDK, writes structured output,
and uses the configured runtime services for Logbook entries and a published
`summary.json` artifact. The Daemon supplies startup configuration at
`RINGFOUR_APP_CONFIG`. In authoring v2, `configuration.environment` may map
declared startup keys to literal environment variables, for example
`"SAMPLE_COUNT": "sample_count"`. Names must be unique, match the uppercase
environment-name pattern, and cannot use `RINGFOUR_`, `PATH`, `LD_*`,
`PYTHONPATH` or `PYTHONHOME`. Values are passed literally; mappings cannot
change the container command, entrypoint, mounts, devices, ports or network.
Agent address comes from `RINGFOUR_AGENT_URL`.

Optional `runtime_settings` declares a second flat object with `schema` and
`defaults`. Use `ringfour_runtime.settings` to validate and apply a complete
object, then acknowledge it from the App. Startup configuration is supplied
when a process starts; live settings can target that exact running process.
Initial live settings must be acknowledged before behavior that depends on
them. Device delivery is at least once, so handlers should make effects
idempotent. `SettingsRejected` takes a bounded safe reason; validation errors
must never be treated as applied values. Live settings are ordinary retained
configuration and Logbook data, not secret storage. Values are limited to the
closed, flat string/integer/number/boolean schema subset, up to 32 keys and
16 KiB runtime messages. `number` means finite IEEE-754 binary64, bounded to
±9,007,199,254,740,991 and serialized with shortest round-trip decimal form.
NaN, infinities, overflow, underflow, boolean/string coercion are rejected.
See [App runtime](apps-runtime.md) and the
[runtime package guide](../packages/python-runtime/README.md) for recovery,
acknowledgement and helper usage.

Published artifacts are immutable outputs, limited to 16 files and 16 MiB per
file. The per-run artifact directory is a publication source,
not persistent App working storage: caches, databases and downloaded models do
not survive as an installation disk.
