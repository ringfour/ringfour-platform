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
