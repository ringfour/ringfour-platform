# Single sequential coding agent

Implement Ringfour v0.1 using README.md, docs/architecture.md and ordered docs/milestones/00–10. Work as one agent; do not delegate or parallelize milestone work.

1. Inspect the repository and applicable instructions. Read the current milestone and prerequisite contracts. Record unknowns rather than inventing existing APIs.
2. Define/update OpenAPI, JSON Schemas, state transitions, errors and representative fixtures first. Classify new Ringfour interfaces as new design.
3. Implement the smallest complete slice across only affected components. No speculative abstractions or mandatory Console parity.
4. Run affected component tests independently. Then run actual components over the specified protocols for integrated acceptance; mocks do not count as E2E.
5. Check authorization, restart recovery and negative paths required by the milestone. Resolve regressions before advancing.
6. Record exact setup/test commands, dependency versions, outcomes and any external device evidence in docs/evidence/NN.md. Redact tokens and secrets.
7. Review the diff, update contracts/docs, and commit the completed milestone. Never mark a skipped external gate as passing. If credentials, repositories or hardware are unavailable, identify the missing prerequisite and leave that gate incomplete.

Keep applications independently packageable. Do not import their private implementation across boundaries. Keep SDK and Agent in their existing projects; inspect their real documentation/source and pin tested versions before integrating. Do not invent SDK calls, duplicate ROS logic or move robot profile ownership to Ringfour.

Console (and any future Ringfour web UI) follows the Ringfour interface system in the sibling repository `ringfour/design/design_system` (tokens.css/base.css/components.css): its color roles (orange = brand/actions, teal = ready/success, red = error, neutral surfaces), square panels with restrained elevation, 6px action / 4px field radii, Space Grotesk titles and Inter body text. Vendor required assets (fonts) into the consuming app; do not cross-import the design project's implementation.

Build the Platform API in C# on supported .NET LTS using ASP.NET Core, EF Core/Npgsql and PostgreSQL. Keep a modular monolith with ordinary services and built-in dependency injection. Use EF Core migrations and xUnit with PostgreSQL Testcontainers. Avoid CQRS frameworks, MediatR and event sourcing. Hosted services may process durable workflow state; persist critical state and retries in PostgreSQL. Keep OpenAPI/JSON Schema and consumers independent of backend implementation.

Follow the [starter database schema](docs/database-schema.md). Add tables in their assigned milestone. Document schema deviations and test the preserved invariants in the same milestone. For later slices, specify persistence alongside the public contract before implementation.

Use Auth0 JWT Bearer with RS256 and audience `https://api.ringfour.eu`; CLI uses OAuth Device Authorization Flow, and Console uses the Ringfour Web SPA with Authorization Code + PKCE. Map `sub` to local users and enforce organization roles in Ringfour. No Auth0 Organizations or human tokens for daemon/Runner.

Preserve mandatory Auth0, hosted-first architecture, fixed separate Runner and uv environments. Never mount the Docker socket. Infrastructure manages containers. Do not add authentication bypasses for the internal Console. Local test signing keys are test-only and must be rejected by hosted configuration.

Do not start the next milestone with failing acceptance. Contract changes require all affected consumers and tests to change in the same milestone. Avoid unrelated refactors. Trusted-code execution is the v0.1 assumption; do not describe virtual environments as a security sandbox.
