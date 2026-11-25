# Platform applications

| Application | Current responsibility |
| --- | --- |
| [API](api/README.md) | Authenticated workspaces, App catalog, installations, hosted runs and history |
| [CLI](cli/README.md) | Auth0 login, organization/device inspection and App authoring/publication |
| [Console](console/README.md) | Authenticated internal web interface; App workflows are in AP-11 |
| [Daemon](daemon/README.md) | Enrolled-device identity, operations, App engine and runtime evidence |

Applications communicate through versioned contracts in
[`packages/contracts`](../packages/contracts). They do not import another
application's private implementation. The human API and Console use Auth0;
robot Daemons use their enrolled machine credentials.

See the [repository overview](../README.md),
[architecture](../docs/architecture.md), and
[development setup](../docs/development.md). Historical mission-era user
workflows are in the [archive](../docs/archive/legacy-mission-workflows/README.md).
