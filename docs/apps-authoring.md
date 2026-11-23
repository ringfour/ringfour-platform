# App authoring and publication — AP-06 new design

The user authorized AP-06 component work before deployed AP-05 acceptance on
23 November 2026. AP-05 registry/Auth0 external checks stay pending. The operator
subsequently reloaded both stacks; readiness and anonymous Store 401 are verified.

`app.json` follows the authoring v1 schema: manifest metadata/capabilities/config,
editable target repositories, explicit Dockerfile and source-file allowlist,
and a checksum-pinned AP-04 local wheel bundle. It is not publication input.
`app.manifest.json` follows manifest v1 and contains only concrete image digests.

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

`app publish PATH [--org UUID] [--visibility listed|unlisted]` uses Docker's normal
registry login, verifies the unchanged build inputs/local IDs, pushes images,
resolves/validates concrete remote digest/platform/config, and freezes the release
before calling AP-05. API credentials stay in the existing Auth0 keyring flow.
Registry success followed by API failure retains the same frozen manifest and
stable request key. Different source content at an already frozen release requires
an explicit version bump. Prior release files are immutable; output may be inspected
after refresh. `--manifest FILE` publishes independently prepared immutable images
without requiring a local build. API metadata is published last.

Commands support `--json` results and bounded structured errors/nonzero exits.
Offline validation requires neither login nor running services. Build/publish never
reload Platform or virtual stacks. Installation/start/stop/Store UI commands remain
AP-07/AP-09/AP-11/AP-12 work; publishing does not start robot work.

Robot Check uses a single `sample_count` integer (1–20, default 1), matching the
AP-01 fixture. It reads only verified SDK `robot.imu()` with NET transport and
requires advancing finite sample stamps within a bounded wait. SIGTERM/SIGINT
stop its loop. It emits timestamped JSON, and writes recognizable Logbook entries
only when execution runtime URLs/credentials are supplied. No runtime configuration
means stdout-only standalone use. App configuration is a JSON file selected by
owner-supplied `RINGFOUR_APP_CONFIG`, defaulting to the image's example file;
AP-08 will wire owner-controlled configuration delivery. Agent address comes from
`RINGFOUR_AGENT_URL`. Run-scoped runtime delivery remains an AP-10 gate.
