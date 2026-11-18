import assert from "node:assert/strict";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { scanArchitecture } from "./scanner.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const platformRoot = path.resolve(here, "../..");
const workspaceRoot = path.resolve(platformRoot, "..");

test("scan returns the core execution path", async () => {
  const graph = await scanArchitecture({ platformRoot, workspaceRoot });
  const ids = new Set(graph.nodes.map((node) => node.id));
  for (const id of ["platform-api", "daemon", "runner", "mission", "sdk", "agent", "contracts", "postgres", "daemon-sqlite", "runner-sqlite"]) {
    assert.ok(ids.has(id), `missing ${id}`);
  }
  assert.ok(graph.edges.some((edge) => edge.source === "daemon" && edge.target === "runner"));
  assert.ok(graph.meta.filesScanned > 20);
});

test("scan derives routes, schemas, and all three persistence owners", async () => {
  const graph = await scanArchitecture({ platformRoot, workspaceRoot });
  assert.ok(graph.meta.routes.api > 0);
  assert.ok(graph.meta.routes.runner > 0);
  assert.ok(graph.meta.routes.openapi > 0);
  assert.ok(graph.nodes.some((node) => node.id.startsWith("contract:schema:")));
  for (const database of ["postgres", "daemon-sqlite", "runner-sqlite"]) {
    assert.ok(graph.nodes.some((node) => node.kind === "table" && node.owner === database), `no tables for ${database}`);
  }
});

test("every relationship references existing nodes", async () => {
  const graph = await scanArchitecture({ platformRoot, workspaceRoot });
  const ids = new Set(graph.nodes.map((node) => node.id));
  for (const edge of graph.edges) {
    assert.ok(ids.has(edge.source), `${edge.id} has missing source ${edge.source}`);
    assert.ok(ids.has(edge.target), `${edge.id} has missing target ${edge.target}`);
  }
});
