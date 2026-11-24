# SDK artifacts

`ringfour_sdk-0.1.0.dev0-py3-none-any.whl` is the unmodified wheel from the
separate Ringfour SDK project, SHA-256
`c536159e45ddf25301d365e8b9ba7be5209711153d664ae9f5391c15a3bfccc1`.
It is copied into the Runner image and installed into a mission environment
only when that mission pins it and its dependencies in a hash-checked lock.
The older RoboCore wheel is retained for existing immutable mission revisions;
new missions should use the Ringfour SDK. Agent and profiles remain separate.

`ringfour_platform_runtime-0.1.0.dev0-py3-none-any.whl` is built from the
separate `packages/python-runtime` project, SHA-256
`03cf85314094176071f2ff22169acf4965083211aae4cdb3888a65431a558158`.
Missions install it explicitly through a hash-pinned lock; the standalone SDK
and Agent do not require it. Images must be rebuilt to include it.

`ringfour_ui-0.1.0.dev0-py3-none-any.whl` is built from
`packages/ringfour-ui`. The reproducible wheel built on 2026-11-18 has SHA-256
`2122c8ee0c2b48fe86f6675360008b63612a08632b249e44bde6f868c7867d78`. It
depends on the separately pinned Ringfour SDK and Gradio 6.32.0. Missions that
serve a Gradio UI must pin this wheel and its dependencies in their
hash-checked `requirements.lock`. A `requirements.in` can contain:

```text
ringfour-sdk==0.1.0.dev0
ringfour-ui==0.1.0.dev0
```

Compile it for Python 3.13 with `--find-links` pointing to the local
`apps/runner/vendor` directory, `--emit-find-links`, and `--generate-hashes`.
In the generated lock, change the emitted `--find-links` line to
`--find-links /opt/ringfour/sdk`, the path inside Runner. This pins both local
wheels by their hashes and resolves the full transitive Gradio dependencies.
The UI package is optional and does not add Gradio to the SDK, Runner service
or Platform runtime. Images must be rebuilt to include it.

## Preparing a fresh checkout

The Ringfour wheels above are local build inputs and are not committed to this
repository. The Runner image copies this directory; the optional SDK image
also requires the SDK wheel. `ringfour-virtual/up.sh` builds all three local
wheels into this directory before building the Runner image. To prepare a
fresh checkout manually, supply the SDK wheel from the sibling SDK checkout
and build the optional runtime and UI packages:

```sh
# From the Platform repository root.
SOURCE_DATE_EPOCH="$(git -C ../ringfour-sdk show -s --format=%ct HEAD)" \
  uv build --wheel ../ringfour-sdk --out-dir /tmp/ringfour-sdk-wheel
cp /tmp/ringfour-sdk-wheel/ringfour_sdk-0.1.0.dev0-py3-none-any.whl apps/runner/vendor/
SOURCE_DATE_EPOCH="$(git log -1 --format=%ct -- packages/python-runtime)" \
  uv build packages/python-runtime --out-dir /tmp/ringfour-runtime-wheel
cp /tmp/ringfour-runtime-wheel/ringfour_platform_runtime-0.1.0.dev0-py3-none-any.whl apps/runner/vendor/
SOURCE_DATE_EPOCH="$(git show -s --format=%ct HEAD)" \
  uv build --wheel packages/ringfour-ui --out-dir /tmp/ringfour-ui-wheel
cp /tmp/ringfour-ui-wheel/ringfour_ui-0.1.0.dev0-py3-none-any.whl apps/runner/vendor/
shasum -a 256 apps/runner/vendor/ringfour*.whl
```

Compare the checksums with the tested values above. Rebuilding the SDK wheel
can produce a different hash even at the same version; use the matching tested
artifact or update mission locks and revalidate before using a different one.
