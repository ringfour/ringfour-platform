# Ringfour local Python wheels

This versioned bundle installs without source checkouts or a package feed.
Verify `sha256sum --check SHA256SUMS` before use and retain the manifest from
the trusted producer. Checksums travel with the bundle; they are integrity pins,
not a signature proving who supplied it.

For CPython 3.13 on glibc Linux amd64, from this directory:

```sh
uv venv --python 3.13 .venv
uv pip install --python .venv/bin/python --offline --no-index \
  --find-links linux-amd64/wheels --require-hashes --only-binary=:all: \
  -r linux-amd64/requirements-sdk.txt
.venv/bin/python -c 'from ringfour import Robot; print(Robot.imu)'
```

Use `requirements-runtime.txt` for the optional runtime alone, or
`requirements.txt` for both. On arm64 use `linux-arm64`; dependency wheel
availability is recorded separately from execution qualification.

Standalone image, with the SDK only:

```sh
docker build -t ringfour-sdk:ap04 .
docker run --rm --network none ringfour-sdk:ap04
```

Add `--build-arg INSTALL_RUNTIME=true` to install the optional Platform clients.
The image is a development/consumer image, not a robot service or an App release.
Both base image indexes and dependency wheels are pinned. Images may need to be
pulled before building, but Python package installation itself is offline.

Robot access uses the independent SDK-to-Agent connection. Runtime calls need
Daemon execution URLs and scoped credentials supplied by infrastructure;
importing the clients does not start an execution or contact Platform.

Do not overwrite this bundle or substitute another wheel with the same version.
Changed package content requires a new distribution version and bundle directory.
Public package-feed/release publication is deferred by the local-wheel decision.
