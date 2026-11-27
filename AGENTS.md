# Platform development

Work sequentially. Preserve the independently packaged applications, current Apps contracts, authorization, and restart-recovery behavior. Review the affected contracts and run the component checks in [development.md](docs/development.md). Record external checks that could not run.

The operator performs any reload, restart, recreation or rebuild of the Platform or virtual stack.

Keep applications independently packageable. Do not import their private implementation across boundaries. Keep SDK and Agent in their existing projects; inspect their real documentation/source and pin tested versions before integrating. Do not invent SDK calls, duplicate ROS logic or move robot profile ownership to Ringfour.

Console (and any future Ringfour web UI) follows the Ringfour interface system in the sibling repository `../design_system` (tokens.css/base.css/components.css): its color roles (orange = brand/actions, teal = ready/success, red = error, neutral surfaces), square panels with restrained elevation, 6px action / 4px field radii, Space Grotesk titles and Inter body text. Vendor required assets (fonts) into the consuming app; do not cross-import the design project's implementation.

Build the Platform API in C# on supported .NET LTS using ASP.NET Core, EF Core/Npgsql and PostgreSQL. Keep a modular monolith with ordinary services and built-in dependency injection. Use EF Core migrations and xUnit with PostgreSQL Testcontainers. Avoid CQRS frameworks, MediatR and event sourcing. Hosted services may process durable workflow state; persist critical state and retries in PostgreSQL. Keep OpenAPI/JSON Schema and consumers independent of backend implementation.

Follow the [starter database schema](docs/database-schema.md). Document schema changes and test the affected invariants alongside the public contract.

Use Auth0 JWT Bearer with RS256 and audience `https://api.ringfour.eu`; CLI uses OAuth Device Authorization Flow, and Console uses the Ringfour Web SPA with Authorization Code + PKCE. Map `sub` to local users and enforce organization roles in Ringfour. No Auth0 Organizations or human tokens for the daemon.

Preserve mandatory Auth0 and hosted-first architecture. Infrastructure owns Daemon and Agent containers. Only the authorized Daemon may manage App workloads for its enrolled robot through an operator-provided engine socket. That socket grants engine authority; containers sharing an engine trust its administrators. Never mount the socket into an App, Runner or server. App deployment settings are owner-controlled; manifests cannot supply mounts, privileges, devices, networks, commands or entrypoint overrides. The fixed Runner, mission/script clients and managed UI are retired. The Agent retains ROS and safety ownership. Do not add authentication bypasses for the internal Console. Local test signing keys are test-only and must be rejected by hosted configuration.

Update all affected consumers and tests when a contract changes. Avoid unrelated refactors. Trusted-code execution is the v0.1 assumption; do not describe virtual environments as a security sandbox.
