# Ringfour architecture explorer

A local, read-only view of the Ringfour backend, contracts, persistence, and
device-side execution path. The graph is rebuilt from the working tree every
time the browser refreshes it; there is no generated architecture file to keep
in sync.

## Run

```bash
cd tools/architecture-explorer
npm start
```

Open `http://127.0.0.1:4177`. To use another port:

```bash
PORT=4180 npm start
```

The tool only reads source files. It does not start or communicate with the
Ringfour Platform or `ringfour-virtual` stacks.

## What is inferred

- C# Minimal API route registrations and feature folders
- FastAPI route decorators in the Daemon and Runner
- OpenAPI documents and JSON Schema families
- EF Core `DbSet`/`ToTable` declarations
- SQLite `CREATE TABLE` statements in migrations and Python modules
- source-level HTTP, WebSocket, Runner, SDK, and Agent connection evidence

The small System lens is curated in `scanner.mjs` so it remains legible. Its
source references and the detailed lenses are derived from the current checkout.
