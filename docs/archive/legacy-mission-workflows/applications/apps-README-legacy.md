# Ringfour applications

This directory contains independently packaged parts of the platform:

| Application | Responsibility |
| --- | --- |
| [API](../../../../apps/api/README.md) | Hosted identity, organization access, robot state, mission coordination and storage metadata |
| [CLI](cli-legacy-README.md) | Developer commands for publishing and operating missions and inspecting output |
| [Console](../../../../apps/console/README.md) | Internal development UI for the API |
| [Daemon](../../../../apps/daemon/README.md) | Robot identity, outbound connection and local coordination |
| [Runner](runner-README.md) | Private, fixed execution service that prepares and runs mission code |

The CLI and Console authenticate people through Auth0. The daemon authenticates
as its robot over an outbound connection. It communicates with the Runner over
a separate private authenticated API; the Runner is not exposed to the Platform.
The applications do not import each other's private implementations. Protocols
and schemas live in [`packages/contracts`](../../../../packages/contracts).

Start with the [repository overview](../../../../README.md), [architecture](../contracts/architecture-legacy.md),
and [development setup](../planning/development-legacy.md). The server stack is in
[`infra/server`](../../../../infra/server); the legacy E2E Compose fixture is in
[`tests/integration/compose`](../../../../tests/integration/compose). Base101 simulation,
Agent, daemon, and Runner orchestration live in the sibling
[`ringfour-virtual`](../../../../../ringfour-virtual/README.md) project. Human
verification guides and evidence live in
[`docs/human-testing`](../../../human-testing) and [`docs/evidence`](../../../evidence).
