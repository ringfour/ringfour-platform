# Python artifact distribution

The SDK and the optional Platform runtime ship as independent wheels in a versioned local bundle. SDK calls target the Agent. Runtime clients use run-scoped credentials to reach the Daemon.

## Bundle contract, version 1

`packages/python-artifacts/release.json` pins source commits, build epochs,
package versions, wheel hashes, transitive dependency wheels and base images.
The builder archives those exact Git commits, builds each package with `uv`
and its committed build constraints, verifies the resulting hashes, and fetches
only checksum-pinned third-party wheels. Dirty working copies and Runner's
vendor directory are never used as build inputs.

The output directory must not already exist. It contains:

- `release.json`: exact release inputs and per-target wheel pins.
- `SHA256SUMS`: checksums of all other bundle files, using relative paths.
- `Dockerfile`: standalone SDK consumer; runtime installation is optional.
- `linux-amd64/` and `linux-arm64/`: `wheels/`, `requirements-sdk.txt`,
  `requirements-runtime.txt` and combined `requirements.txt`.

Each lock pins every required distribution and SHA-256. Installation uses
`--offline --no-index --require-hashes --only-binary=:all:` with `uv`; no network,
editable install, source checkout, ROS installation or Runner mount is needed.
The builder needs network access to download third-party/build dependencies;
consumer installation does not. Pulling the pinned Python/uv base images still
requires registry access unless already cached.

The Agent wire protocol is version 1. See the release manifest for the individual wheel versions. Older local
development wheels have the same version but are not this release: use the
recorded checksum, never the filename alone. Once distributed, retain these
bytes. Changed package content requires a new distribution version and a new
bundle directory; never rename a changed wheel to its old version. Package
version, wheel checksum, Agent protocol, base image digest and future App
revision version are separate identifiers. This bundle is not an App image or
Store release.

Bundle `0.1.1.dev0` includes runtime settings support. Apps declaring live settings require a settings-capable bundle. The [release manifest](../packages/python-artifacts/release.json) pins the individual package versions, checksums and commits. The local release is installed at
`/home/cdr/.local/share/ringfour/python-artifacts/0.1.1.dev0`.

Locks and dependency wheels target CPython 3.13 on glibc Linux amd64/arm64.
The SDK's pure Python wheel still declares Python >=3.10; that does not qualify
these dependency locks on another interpreter/platform. amd64 consumer execution
and arm64 wheel availability are recorded separately; no arm64 runtime claim is
made without executing that target.

## Build and consume

From Platform, using `uv` 0.13.8 and Python 3.13.18 for package builds, with
the pinned SDK commit available in `../ringfour-sdk`. The orchestration script
requires Python >=3.12; `uv` can provision its pinned build interpreter:

```sh
python3 scripts/build-python-artifacts.py --output /tmp/ringfour-python-0.1.1.dev0
cd /tmp/ringfour-python-0.1.1.dev0
sha256sum --check SHA256SUMS
docker build -t ringfour-sdk:local .
docker run --rm --network none ringfour-sdk:local
docker build --build-arg INSTALL_RUNTIME=true -t ringfour-sdk-runtime:local .
docker run --rm --network none ringfour-sdk-runtime:local python -c \
  'from ringfour import Robot; from ringfour_runtime import artifacts, interactions, logbook, settings; print(Robot.imu)'
```

These are standalone artifact checks, not Platform/virtual stack operations.
An SDK-only installation omits the optional runtime. To install only the runtime
in a virtual environment, select `requirements-runtime.txt` instead of the
SDK/combined lock. Transfer the complete bundle and its trusted checksum manifest
to another build host; no private repository credentials need to travel with it.

`packages/sdk-image/Dockerfile` is also usable directly with this bundle as its
build context. It no longer accepts the Platform root/Runner wheelhouse as input.
