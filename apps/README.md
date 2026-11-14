# Ringfour applications

This directory contains independently packaged parts of the platform:

| Application | Responsibility |
| --- | --- |
| [API](api/README.md) | Hosted identity, organization access, robot state, mission coordination and storage metadata |
| [CLI](cli/README.md) | Developer commands for publishing and operating missions and inspecting output |
| [Console](console/README.md) | Internal development UI for the API |
| [Daemon](daemon/README.md) | Robot identity, outbound connection and local coordination |
| [Runner](runner/README.md) | Private, fixed execution service that prepares and runs mission code |

The CLI and Console authenticate people through Auth0. The daemon authenticates
as its robot over an outbound connection. It communicates with the Runner over
a separate private authenticated API; the Runner is not exposed to the Platform.
The applications do not import each other's private implementations. Protocols
and schemas live in [`packages/contracts`](../packages/contracts/).

Start with the [repository overview](../README.md), [architecture](../docs/architecture.md),
and [development setup](../docs/development.md). The local Compose stack is in
[`infra/compose`](../infra/compose/); the separate robot fixture and Balena
configuration are under [`infra`](../infra/). Human verification guides and
actual test evidence live in [`docs/human-testing`](../docs/human-testing/) and
[`docs/evidence`](../docs/evidence/).
