# SDK artifacts

`ringfour_sdk-0.1.0.dev0-py3-none-any.whl` is the unmodified wheel from the
separate Ringfour SDK project, SHA-256
`f6f1519e5fb72c6cd05367fc2fff1afa824e287a78a72a33636609dafaf8e459`.
It is copied into the Runner image and installed into a mission environment
only when that mission pins it and its dependencies in a hash-checked lock.
The older RoboCore wheel is retained for existing immutable mission revisions;
new missions should use the Ringfour SDK. Agent and profiles remain separate.

`ringfour_platform_runtime-0.1.0.dev0-py3-none-any.whl` is built from the
separate `packages/python-runtime` project, SHA-256
`781fcebf64494ed5106f47aa4a656a37fc7aef5ed32bd4ce6f0ed77e8d8972b7`.
Missions install it explicitly through a hash-pinned lock; the standalone SDK
and Agent do not require it. Images must be rebuilt to include it.

## Preparing a fresh checkout

The two Ringfour wheels above are local build inputs and are not committed to
this repository. The Runner image copies this directory; the optional SDK image
also requires the SDK wheel. Before building either image, supply the tested
SDK wheel from the sibling SDK checkout and build the optional runtime:

```sh
# From the Platform repository root. The SDK dist wheel must be available.
cp ../ringfour-sdk/dist/ringfour_sdk-0.1.0.dev0-py3-none-any.whl apps/runner/vendor/
uv build packages/python-runtime --out-dir /tmp/ringfour-runtime-wheel
cp /tmp/ringfour-runtime-wheel/ringfour_platform_runtime-0.1.0.dev0-py3-none-any.whl apps/runner/vendor/
shasum -a 256 apps/runner/vendor/ringfour*.whl
```

Compare the checksums with the tested values above. Rebuilding the SDK wheel
can produce a different hash even at the same version; use the matching tested
artifact or update mission locks and revalidate before using a different one.
