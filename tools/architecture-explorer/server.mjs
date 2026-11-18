import http from "node:http";
import { readFile, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { scanArchitecture } from "./scanner.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const platformRoot = path.resolve(here, "../..");
const workspaceRoot = path.resolve(platformRoot, "..");
const publicRoot = path.join(here, "public");
const port = Number.parseInt(process.env.PORT || "4177", 10);
const host = process.env.HOST || "127.0.0.1";

const contentTypes = {
  ".html": "text/html; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".woff2": "font/woff2",
};

function send(response, status, body, type = "application/json; charset=utf-8") {
  response.writeHead(status, {
    "content-type": type,
    "cache-control": "no-store",
    "x-content-type-options": "nosniff",
  });
  response.end(body);
}

function safeSourcePath(candidate) {
  if (!candidate || path.isAbsolute(candidate)) return null;
  const resolved = path.resolve(workspaceRoot, candidate);
  const relative = path.relative(workspaceRoot, resolved);
  if (relative.startsWith("..") || path.isAbsolute(relative)) return null;
  return resolved;
}

async function sourceExcerpt(url, response) {
  const requested = url.searchParams.get("path");
  const resolved = safeSourcePath(requested);
  if (!resolved) return send(response, 400, JSON.stringify({ error: "Invalid source path." }));
  try {
    const info = await stat(resolved);
    if (!info.isFile() || info.size > 2_000_000) throw new Error("unsupported");
    const lines = (await readFile(resolved, "utf8")).split(/\r?\n/);
    const focus = Math.max(1, Number.parseInt(url.searchParams.get("line") || "1", 10));
    const start = Math.max(1, focus - 7);
    const end = Math.min(lines.length, focus + 12);
    send(response, 200, JSON.stringify({
      path: requested,
      focus,
      start,
      end,
      lines: lines.slice(start - 1, end),
    }));
  } catch {
    send(response, 404, JSON.stringify({ error: "Source file is unavailable." }));
  }
}

async function staticFile(url, response) {
  const pathname = url.pathname === "/" ? "/index.html" : url.pathname;
  const fontFiles = {
    "/fonts/SpaceGrotesk-Medium.woff2": path.join(platformRoot, "apps/daemon/src/ringfour_daemon/static/SpaceGrotesk-Medium.woff2"),
    "/fonts/inter-latin-400-normal.woff2": path.join(platformRoot, "apps/daemon/src/ringfour_daemon/static/inter-latin-400-normal.woff2"),
  };
  if (fontFiles[pathname]) {
    try { return send(response, 200, await readFile(fontFiles[pathname]), "font/woff2"); }
    catch { return send(response, 404, "Not found", "text/plain; charset=utf-8"); }
  }
  const resolved = path.resolve(publicRoot, `.${pathname}`);
  if (!resolved.startsWith(`${publicRoot}${path.sep}`)) {
    return send(response, 403, "Forbidden", "text/plain; charset=utf-8");
  }
  try {
    const body = await readFile(resolved);
    send(response, 200, body, contentTypes[path.extname(resolved)] || "application/octet-stream");
  } catch {
    send(response, 404, "Not found", "text/plain; charset=utf-8");
  }
}

const server = http.createServer(async (request, response) => {
  const url = new URL(request.url || "/", `http://${request.headers.host || `${host}:${port}`}`);
  try {
    if (request.method === "GET" && url.pathname === "/api/graph") {
      const graph = await scanArchitecture({ platformRoot, workspaceRoot });
      return send(response, 200, JSON.stringify(graph));
    }
    if (request.method === "GET" && url.pathname === "/api/source") {
      return sourceExcerpt(url, response);
    }
    if (request.method !== "GET") {
      return send(response, 405, JSON.stringify({ error: "Method not allowed." }));
    }
    return staticFile(url, response);
  } catch (error) {
    console.error(error);
    return send(response, 500, JSON.stringify({ error: "Architecture scan failed." }));
  }
});

server.listen(port, host, () => {
  console.log(`Ringfour architecture explorer: http://${host}:${port}`);
  console.log(`Reading workspace: ${workspaceRoot}`);
});
