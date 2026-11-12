# RoboCore SDK artifact

`robocore-0.1.0.dev20261111114406+c.799c8afa-py3-none-any.whl` is the
unchanged C-preview artifact produced by the separate RoboCore SDK project. Its
SHA-256 is `2c41f42c81ee82d8b8fe13535c1feb48de8dd19df7bf126a650bdda2a5592a00`.
It was frozen from a dirty source snapshot based on commit `799c8afa`, so the
commit alone does not identify the build. The source project's
`artifacts/robocore-c-preview/manifest.json` identifies the frozen files. It
matches the `robocore-agent:c-preview` image used by the robot deployment.

The Runner image carries this wheel; it is installed into a mission's isolated
uv environment only when explicitly listed with its hash in that mission's
requirements lock. The SDK project and Agent remain separate and own their APIs
and robot profiles.
