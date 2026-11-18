import { readdir, readFile, stat } from "node:fs/promises";
import path from "node:path";

const SOURCE_EXTENSIONS = new Set([".cs", ".py", ".sql", ".yaml", ".yml", ".json", ".md"]);
const IGNORED = new Set([".git", ".venv", "node_modules", "bin", "obj", "dist", ".pytest_cache", ".ruff_cache", "__pycache__"]);

const COMPONENTS = [
  { id: "platform-api", label: "Platform API", kind: "service", layer: "Hosted backend", summary: "ASP.NET Core modular monolith; owns hosted workflows and authorization.", views: ["system", "backend", "contracts", "data"], source: "ringfour-platform/apps/api/src/Ringfour.Api/Program.cs" },
  { id: "daemon", label: "Robot Daemon", kind: "service", layer: "Device coordination", summary: "Owns the outbound device connection, durable command coordination, and Runner calls.", views: ["system", "backend", "contracts", "data"], source: "ringfour-platform/apps/daemon/src/ringfour_daemon/main.py" },
  { id: "runner", label: "Runner", kind: "service", layer: "Device execution", summary: "Owns environments, processes, execution state, output, and local artifacts.", views: ["system", "backend", "contracts", "data"], source: "ringfour-platform/apps/runner/src/ringfour_runner/main.py" },
  { id: "mission", label: "Mission process", kind: "runtime", layer: "Execution", summary: "A non-root Python process launched by Runner from a prepared revision.", views: ["system", "backend"], source: "ringfour-platform/apps/runner/src/ringfour_runner/executions.py" },
  { id: "sdk", label: "Ringfour SDK", kind: "external", layer: "Robot integration", summary: "Mission-facing Python interface used to reach robot capabilities.", views: ["system", "backend"], source: "ringfour-sdk/ringfour/client.py" },
  { id: "agent", label: "Robot Agent", kind: "external", layer: "Robot integration", summary: "Existing robot-side Agent bridging the SDK to ROS and hardware.", views: ["system", "backend"], source: "ringfour-agent/src/ringfour_agent/package.xml" },
  { id: "ros", label: "ROS / robot", kind: "external", layer: "Robot", summary: "Robot middleware, simulator, sensors, and actuators outside the Platform boundary.", views: ["system"], source: "ringfour-platform/docs/architecture.md" },
  { id: "contracts", label: "Contracts package", kind: "contract", layer: "Shared interfaces", summary: "Language-independent OpenAPI documents, JSON Schemas, and fixtures.", views: ["system", "contracts"], source: "ringfour-platform/packages/contracts/package.json" },
  { id: "postgres", label: "PostgreSQL", kind: "database", layer: "Hosted persistence", summary: "Accounts, organizations, robots, durable workflows, deployments, and replicated history.", views: ["system", "backend", "data"], source: "ringfour-platform/apps/api/src/Ringfour.Api/Persistence/RingfourDbContext.cs" },
  { id: "daemon-sqlite", label: "Daemon SQLite", kind: "database", layer: "Device persistence", summary: "Device identity, deduplication, coordination state, local events, and outboxes.", views: ["system", "backend", "data"], source: "ringfour-platform/apps/daemon/src/ringfour_daemon/db.py" },
  { id: "runner-sqlite", label: "Runner SQLite", kind: "database", layer: "Execution persistence", summary: "Execution metadata, process reconciliation, events, dev sessions, and artifacts.", views: ["system", "backend", "data"], source: "ringfour-platform/apps/runner/src/ringfour_runner/executions.py" },
];

const SYSTEM_EDGES = [
  ["platform-api", "daemon", "device protocol", "TLS WebSocket", "Outbound authenticated connection; commands and status envelopes."],
  ["daemon", "runner", "private control", "HTTP + bearer", "Daemon coordinates durable work against Runner's private API."],
  ["runner", "mission", "launches", "OS process", "Prepared Python revision runs in its own process group."],
  ["mission", "sdk", "uses", "Python API", "Mission code calls the Ringfour SDK."],
  ["sdk", "agent", "connects", "Agent protocol", "SDK reaches capabilities exposed by the existing Agent."],
  ["agent", "ros", "bridges", "ROS", "Agent owns robot/ROS integration."],
  ["platform-api", "postgres", "persists", "EF Core / Npgsql", "Hosted durable state and replicated device history."],
  ["daemon", "daemon-sqlite", "persists", "sqlite3", "Local durable coordination and outbox state."],
  ["runner", "runner-sqlite", "persists", "sqlite3", "Execution lifecycle and restart reconciliation."],
  ["contracts", "platform-api", "defines", "OpenAPI + JSON Schema", "Public and device-facing hosted interfaces."],
  ["contracts", "daemon", "defines", "OpenAPI + JSON Schema", "Device, runtime, and local-development payloads."],
  ["contracts", "runner", "defines", "OpenAPI + JSON Schema", "Private Runner request and response payloads."],
];

const SYSTEM_EVIDENCE = [
  "ringfour-platform/apps/daemon/src/ringfour_daemon/connection.py",
  "ringfour-platform/apps/daemon/src/ringfour_daemon/runner.py",
  "ringfour-platform/apps/runner/src/ringfour_runner/executions.py",
  "ringfour-platform/docs/architecture.md",
  "ringfour-sdk/ringfour/client.py",
  "ringfour-platform/docs/architecture.md",
  "ringfour-platform/apps/api/src/Ringfour.Api/Persistence/RingfourDbContext.cs",
  "ringfour-platform/apps/daemon/src/ringfour_daemon/db.py",
  "ringfour-platform/apps/runner/src/ringfour_runner/executions.py",
  "ringfour-platform/packages/contracts/openapi/ringfour-api.v1.yaml",
  "ringfour-platform/packages/contracts/openapi/ringfour-runtime.v1.yaml",
  "ringfour-platform/packages/contracts/openapi/ringfour-runner.v1.yaml",
];

function toWorkspacePath(workspaceRoot, absolutePath) {
  return path.relative(workspaceRoot, absolutePath).split(path.sep).join("/");
}

async function walk(root) {
  const files = [];
  async function visit(directory) {
    let entries;
    try { entries = await readdir(directory, { withFileTypes: true }); } catch { return; }
    for (const entry of entries) {
      if (IGNORED.has(entry.name)) continue;
      const target = path.join(directory, entry.name);
      if (entry.isDirectory()) await visit(target);
      else if (entry.isFile() && SOURCE_EXTENSIONS.has(path.extname(entry.name))) files.push(target);
    }
  }
  await visit(root);
  return files;
}

async function readSource(file, workspaceRoot) {
  const info = await stat(file);
  if (info.size > 1_500_000) return null;
  return { absolute: file, path: toWorkspacePath(workspaceRoot, file), text: await readFile(file, "utf8") };
}

function lineOf(text, index) {
  return text.slice(0, index).split("\n").length;
}

function sourceRef(file, line = 1, label = "Source") {
  return { path: file, line, label };
}

function componentNodes() {
  return COMPONENTS.map((item) => ({
    ...item,
    sources: [sourceRef(item.source)],
    facts: [],
  }));
}

function edge(id, source, target, kind, protocol, summary, sources = [], views = ["system"]) {
  return { id, source, target, kind, label: kind, protocol, summary, sources, views };
}

function scanCSharpRoutes(source) {
  const routes = [];
  const groupPrefixes = new Map();
  for (const match of source.text.matchAll(/(?:var\s+)?(\w+)\s*=\s*\w+\.MapGroup\(\s*"([^"]+)"/g)) {
    groupPrefixes.set(match[1], match[2]);
  }
  for (const match of source.text.matchAll(/(\w+)\.Map(Get|Post|Put|Delete|Patch)\(\s*"([^"]+)"/g)) {
    routes.push({ method: match[2].toUpperCase(), path: `${groupPrefixes.get(match[1]) || ""}${match[3]}`, line: lineOf(source.text, match.index) });
  }
  return routes;
}

function scanPythonRoutes(source) {
  return [...source.text.matchAll(/@router\.(get|post|put|delete|patch)\(\s*[f]?"([^"]+)"/g)]
    .map((match) => ({ method: match[1].toUpperCase(), path: match[2], line: lineOf(source.text, match.index) }));
}

function titleCase(value) {
  return value.replace(/[-_]+/g, " ").replace(/\b\w/g, (letter) => letter.toUpperCase());
}

function moduleId(owner, name) {
  return `module:${owner}:${name.toLowerCase().replace(/[^a-z0-9]+/g, "-")}`;
}

function addModule(nodes, edges, owner, name, sources, routes, summary) {
  const id = moduleId(owner, name);
  nodes.push({
    id,
    label: titleCase(name),
    kind: "module",
    layer: owner === "platform-api" ? "Hosted module" : owner === "daemon" ? "Daemon module" : "Runner module",
    summary,
    views: ["backend"],
    sources: sources.map((source) => sourceRef(source.path, source.line || 1, source.label || "Implementation")),
    facts: routes.map((route) => `${route.method} ${route.path}`),
    owner,
  });
  edges.push(edge(`contains:${owner}:${id}`, owner, id, "contains", "source", `${titleCase(name)} belongs to ${owner}.`, nodes.at(-1).sources, ["backend"]));
  return id;
}

function contractFamilyFromPath(file) {
  const marker = "/schemas/";
  const index = file.indexOf(marker);
  return index === -1 ? null : file.slice(index + marker.length).split("/")[0];
}

function scanOpenApi(source) {
  const endpoints = [];
  let currentPath = null;
  for (const [index, line] of source.text.split(/\r?\n/).entries()) {
    const route = line.match(/^  (\/[^:]+):\s*$/);
    if (route) currentPath = route[1];
    const method = line.match(/^    (get|post|put|delete|patch):\s*$/);
    if (currentPath && method) endpoints.push({ method: method[1].toUpperCase(), path: currentPath, line: index + 1 });
  }
  return endpoints;
}

function tableMatches(text) {
  const matches = [];
  const patterns = [
    /ToTable\(\s*"([a-zA-Z0-9_]+)"/g,
    /CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?["`]?([a-zA-Z0-9_]+)["`]?/gi,
  ];
  for (const pattern of patterns) {
    for (const match of text.matchAll(pattern)) matches.push({ name: match[1], index: match.index });
  }
  return matches;
}

function inferDatabase(sourcePath) {
  if (sourcePath.includes("/apps/api/")) return "postgres";
  if (sourcePath.includes("/apps/daemon/")) return "daemon-sqlite";
  if (sourcePath.includes("/apps/runner/")) return "runner-sqlite";
  return null;
}

export async function scanArchitecture({ platformRoot, workspaceRoot }) {
  const started = Date.now();
  const roots = [
    path.join(platformRoot, "apps/api/src/Ringfour.Api"),
    path.join(platformRoot, "apps/daemon/src/ringfour_daemon"),
    path.join(platformRoot, "apps/runner/src/ringfour_runner"),
    path.join(platformRoot, "packages/contracts"),
  ];
  const allFiles = (await Promise.all(roots.map(walk))).flat();
  const sources = (await Promise.all(allFiles.map((file) => readSource(file, workspaceRoot)))).filter(Boolean);
  const nodes = componentNodes();
  const edges = SYSTEM_EDGES.map((item, index) => {
    const [source, target] = item;
    const views = ["system"];
    if (source === "contracts") views.push("contracts");
    if (["postgres", "daemon-sqlite", "runner-sqlite"].includes(target)) views.push("backend", "data");
    else if (source !== "contracts") views.push("backend");
    return edge(`system:${index}`, ...item, [sourceRef(SYSTEM_EVIDENCE[index], 1, "Connection evidence")], views);
  });
  const routeCount = { api: 0, daemon: 0, runner: 0, openapi: 0 };

  const apiGroups = new Map();
  for (const source of sources.filter((item) => item.path.includes("/apps/api/src/Ringfour.Api/") && item.path.endsWith(".cs"))) {
    const routes = scanCSharpRoutes(source);
    const relative = source.path.split("/Ringfour.Api/")[1];
    const group = relative.includes("/") ? relative.split("/")[0] : "Core";
    if (!apiGroups.has(group)) apiGroups.set(group, { sources: [], routes: [] });
    if (routes.length || /Endpoints|Lifecycle|Worker|Service/.test(path.basename(source.path))) {
      apiGroups.get(group).sources.push({ path: source.path, line: routes[0]?.line || 1 });
    }
    apiGroups.get(group).routes.push(...routes.map((route) => ({ ...route, source: source.path })));
    routeCount.api += routes.length;
  }
  for (const [name, group] of apiGroups) {
    if (!group.sources.length && !group.routes.length) continue;
    addModule(nodes, edges, "platform-api", name, group.sources.slice(0, 5), group.routes, `${group.routes.length} discovered hosted route${group.routes.length === 1 ? "" : "s"}.`);
  }

  for (const owner of ["daemon", "runner"]) {
    const marker = `/apps/${owner}/src/ringfour_${owner}/`;
    for (const source of sources.filter((item) => item.path.includes(marker) && item.path.endsWith(".py") && !item.path.includes("/fixtures/"))) {
      const routes = scanPythonRoutes(source);
      const fileName = path.basename(source.path, ".py");
      const relevant = routes.length || /httpx\.|websockets|sqlite3|create_task|subprocess|runner_url|platform_url|APIRouter/.test(source.text);
      if (!relevant || fileName === "__init__") continue;
      const id = addModule(nodes, edges, owner, fileName, [{ path: source.path, line: routes[0]?.line || 1 }], routes, `${routes.length ? `${routes.length} discovered route${routes.length === 1 ? "" : "s"}; ` : ""}${owner === "daemon" ? "device coordination" : "execution lifecycle"} implementation.`);
      routeCount[owner] += routes.length;
      if (owner === "daemon" && /runner_url/.test(source.text)) {
        edges.push(edge(`calls-runner:${id}`, id, "runner", "calls", "private HTTP", "This module calls the private Runner API.", [sourceRef(source.path, lineOf(source.text, source.text.indexOf("runner_url")))], ["backend"]));
      }
      if (owner === "daemon" && /(platform_url|websockets\.connect)/.test(source.text)) {
        const found = source.text.search(/platform_url|websockets\.connect/);
        edges.push(edge(`calls-platform:${id}`, id, "platform-api", "connects", source.text.includes("websockets.connect") ? "WebSocket" : "HTTP", "This module communicates with the hosted Platform.", [sourceRef(source.path, lineOf(source.text, found))], ["backend"]));
      }
      if (owner === "runner" && /(create_subprocess_exec|subprocess)/.test(source.text)) {
        const found = source.text.search(/create_subprocess_exec|subprocess/);
        edges.push(edge(`launches-mission:${id}`, id, "mission", "launches", "OS process", "This module launches or controls mission processes.", [sourceRef(source.path, lineOf(source.text, found))], ["backend"]));
      }
    }
  }

  const openApiSources = sources.filter((item) => item.path.includes("/packages/contracts/openapi/") && /\.ya?ml$/.test(item.path));
  for (const source of openApiSources) {
    const endpoints = scanOpenApi(source);
    routeCount.openapi += endpoints.length;
    const name = path.basename(source.path).replace(/\.ya?ml$/, "").replace(/^ringfour-/, "").replace(/\.v\d+$/, "");
    const id = `contract:openapi:${name}`;
    nodes.push({ id, label: `${titleCase(name)} API`, kind: "contract", layer: "OpenAPI", summary: `${endpoints.length} declared operation${endpoints.length === 1 ? "" : "s"}.`, views: ["contracts"], sources: [sourceRef(source.path, endpoints[0]?.line || 1, "OpenAPI")], facts: endpoints.slice(0, 30).map((route) => `${route.method} ${route.path}`) });
    edges.push(edge(`contract-root:${id}`, "contracts", id, "contains", "OpenAPI", "Versioned API definition.", [sourceRef(source.path)], ["contracts"]));
    const target = name.includes("runner") ? "runner" : name.includes("daemon") || name.includes("runtime") || name.includes("local-development") ? "daemon" : "platform-api";
    edges.push(edge(`contract-defines:${id}`, id, target, "defines", "OpenAPI", `Defines interfaces implemented or consumed by ${target}.`, [sourceRef(source.path)], ["contracts"]));
  }

  const families = new Map();
  for (const source of sources.filter((item) => item.path.includes("/packages/contracts/schemas/") && item.path.endsWith(".json"))) {
    const family = contractFamilyFromPath(source.path);
    if (!family) continue;
    if (!families.has(family)) families.set(family, []);
    families.get(family).push(source);
  }
  for (const [family, items] of [...families.entries()].sort()) {
    const id = `contract:schema:${family}`;
    nodes.push({ id, label: titleCase(family), kind: "schema", layer: "JSON Schema", summary: `${items.length} versioned JSON schema${items.length === 1 ? "" : "s"}.`, views: ["contracts"], sources: items.slice(0, 8).map((item) => sourceRef(item.path, 1, path.basename(item.path))), facts: items.map((item) => path.basename(item.path)) });
    edges.push(edge(`schema-root:${id}`, "contracts", id, "contains", "JSON Schema", "Schema family and fixtures.", [sourceRef(items[0].path)], ["contracts"]));
  }

  const tableMap = new Map();
  for (const source of sources) {
    const database = inferDatabase(source.path);
    if (!database) continue;
    for (const match of tableMatches(source.text)) {
      if (match.name === "__EFMigrationsHistory") continue;
      const key = `${database}:${match.name}`;
      if (!tableMap.has(key)) tableMap.set(key, { database, name: match.name, sources: [] });
      tableMap.get(key).sources.push(sourceRef(source.path, lineOf(source.text, match.index), "Declaration"));
    }
  }
  for (const item of [...tableMap.values()].sort((a, b) => a.name.localeCompare(b.name))) {
    const id = `table:${item.database}:${item.name}`;
    nodes.push({ id, label: item.name, kind: "table", layer: item.database === "postgres" ? "PostgreSQL table" : "SQLite table", summary: `Discovered from ${item.sources.length} source declaration${item.sources.length === 1 ? "" : "s"}.`, views: ["data"], sources: item.sources.slice(0, 6), facts: [], owner: item.database });
    edges.push(edge(`table-owner:${id}`, item.database, id, "owns", "persistence", `${item.database} owns ${item.name}.`, item.sources.slice(0, 2), ["data"]));
  }

  const counts = Object.fromEntries(["service", "module", "contract", "schema", "database", "table", "runtime", "external"].map((kind) => [kind, nodes.filter((node) => node.kind === kind).length]));
  return {
    meta: {
      scannedAt: new Date().toISOString(),
      durationMs: Date.now() - started,
      filesScanned: sources.length,
      routes: routeCount,
      counts,
      root: toWorkspacePath(workspaceRoot, platformRoot),
    },
    nodes,
    edges,
  };
}
