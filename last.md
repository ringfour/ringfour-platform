# Ringfour — Independent Distribution and Version Management

## Objective

Implement three narrowly scoped improvements to the deployed Ringfour product:

1. Self-updating CLI installation.
2. Independent SDK and Agent release artifacts and App-specific toolchain selection.
3. Daemon update awareness in Studio.

Preserve the existing architecture and component-local testing strategy.

## Repositories

- ringfour-platform
- ringfour-sdk
- ringfour-agent

Inspect their current implementations before making changes.

## SDK and Agent releases

Establish independent GitHub Actions build and release workflows in the SDK and Agent repositories.

Requirements:

- Build Python wheels and source distributions.
- Establish a single authoritative package version.
- Validate release tag and package version consistency.
- Execute meaningful repository-local tests.
- Verify installation of the built artifacts outside the source checkout.
- Publish distributions and SHA256 checksums to GitHub Releases.
- Publish only after required checks succeed.
- Do not overwrite existing released artifacts.
- Agent additionally publishes AMD64 and ARM64 ROS-compatible OCI images.
- Preserve ROS package, Python ABI, generated interface and NumPy compatibility requirements.
- Replace Agent Dockerfile sibling SDK source context with an exact, verified released SDK wheel.
- Preserve Agent's explicit SDK dependency.
- Resolve the SDK's existing external pytest discovery dependency without establishing another cross-repository testing framework.

Use ordinary GitHub Actions, setuptools and existing packaging tools. Do not establish a custom package registry.

## App toolchain management

Implement independent App-level SDK and Agent version declarations using the existing App authoring configuration.

Specify exact supported versions, protocol compatibility and reproducible dependency resolution.

Both CLI and Daemon must be capable of retrieving released toolchain artifacts independently of sibling source repositories and Platform release bundles.

Reuse uv and existing artifact caching/checksum infrastructure wherever possible.

Support coexistence of multiple SDK versions.

Preserve immutable published OCI App images. Do not silently install different dependencies into published images.

Agent is shared robot infrastructure with safety responsibilities. An App may declare Agent requirements but cannot authorize replacing shared infrastructure. Detect incompatible versions before execution and provide actionable diagnostics.

Do not make ROS installation a requirement for ordinary SDK authoring or CLI usage.

## CLI installer and updater

Extend the existing cli-installer.sh and CLI implementation.

Decouple CLI installation from the Platform-coupled Python artifact bundle.

Support:

- Version reporting.
- Explicit update checking.
- Explicit CLI self-update.
- Cached, non-blocking update notifications.

Use verified release artifacts and the existing uv tool installation model.

Preserve user authentication tokens, configuration, App environments and cached toolchains.

Handle offline usage, failed downloads, interrupted upgrades and corrupt artifacts without preventing the existing CLI from operating.

Do not embed Auth0 secrets or publishing credentials.

## Daemon update awareness

Inspect the existing robot-update.sh, systemd service and timer.

Reuse existing device version reporting wherever possible.

Expose the installed and recommended compatible Daemon versions in Studio.

Represent current, update available, incompatible and unknown states accurately.

Provide an unobtrusive update nudge in the existing Robot detail interface. Reuse existing attention indicators where appropriate.

Provide instructions for the existing operator-managed update mechanism.

Preserve the operator maintenance gate, robot idle requirements, digest-pinned deployment and readiness validation.

Do not introduce remote shell execution, automatic Agent replacement or a new fleet management subsystem.

## Compatibility and tests

Preserve old App manifests and deployed client compatibility.

Add focused repository-local tests for artifact resolution, version handling, cache integrity, update failures, compatibility checks and Studio update indication.

Continue using conventional GitHub Actions.

Do not rebuild the retired AP campaign or full-system acceptance infrastructure.

## Execution order

1. Inspect all three repositories and identify existing release mechanisms.
2. Present the proposed version and artifact contracts for approval.
3. Implement independent SDK and Agent release workflows.
4. Implement CLI installation/update changes.
5. Implement App toolchain resolution.
6. Implement Daemon update visibility in Studio.
7. Run component tests and validate release artifacts.

## Constraints

This is a stabilization and distribution task, not a general feature-development initiative.

Do not redesign App execution, ROS ownership, robot enrollment, authentication, deployment architecture or existing safety mechanisms.

Do not publish releases, modify the live N100 deployment or update physical robots without explicit approval.

If in doubt, STOP AND ASK.

Specifically ask before changing public contracts, Agent ownership, ROS runtime packaging, release channels, version compatibility semantics or existing deployed behaviour.

Deliver a concise report of changes, test results, unresolved decisions and remaining risks.