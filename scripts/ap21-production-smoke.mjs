import { execFileSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import { mkdir, writeFile } from "node:fs/promises";
import { resolve } from "node:path";

// Uses only a disposable web container; never rebuilds or reloads either stack.
// Build dist with VITE_CONSOLE_ORIGIN=http://localhost:4175 before running.
const root = resolve(new URL("..", import.meta.url).pathname);
const name = "ap21-static-" + randomUUID().slice(0, 8);
const nginx = "nginx:1.28-alpine@sha256:a8b39bd9cf0f83869a2162827a0caf6137ddf759d50a171451b335cecc87d236";
const docker = (...args) => execFileSync("docker", args, { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
const origin = "http://localhost:4175";
try {
  docker("run", "--rm", "-d", "--name", name, "--add-host=host.docker.internal:host-gateway", "-e", "API_PROXY_TARGET=http://host.docker.internal:8080", "-p", "127.0.0.1:4175:80", "-v", root + "/apps/console/dist:/usr/share/nginx/html:ro", "-v", root + "/apps/console/nginx.conf.template:/etc/nginx/templates/default.conf.template:ro", nginx);
  let ready = false;
  for (let attempt = 0; attempt < 50; attempt++) {
    try { if ((await fetch(origin)).ok) { ready = true; break; } } catch {}
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  if (!ready) throw new Error("Disposable static server failed readiness");
  const deep = await fetch(origin + "/apps/camera/settings?workspace=workshop&installation=robot-b");
  const html = await deep.text();
  if (!deep.ok || !html.includes('<div id="root">')) throw new Error("Deep-link fallback failed");
  const missingAsset = await fetch(origin + "/assets/missing.js");
  if (missingAsset.status !== 404) throw new Error("Missing asset returned an HTML fallback");
  const live = await fetch(origin + "/api/health/live");
  if (!live.ok) throw new Error("API proxy did not preserve path stripping");
  const denied = await fetch(origin + "/api/v1/organizations");
  if (denied.status !== 401) throw new Error("Unauthenticated API access was not refused");
  const { chromium } = await import("../apps/console/node_modules/playwright/index.mjs");
  const browser = await chromium.launch();
  try {
    const page = await browser.newPage();
    await page.goto(origin + "/apps/camera/settings?workspace=workshop&installation=robot-b");
    await page.getByRole("button", { name: "Sign in", exact: true }).waitFor();
    if (await page.getByText("Ringfour Console is a development-only tool.").count()) throw new Error("Production guard remains");
    const evidence = resolve(root, "docs/evidence/ap21-browser");
    await mkdir(evidence, { recursive: true });
    await page.screenshot({ path: evidence + "/production-sign-in.png" });
    await writeFile(evidence + "/production-results.json", JSON.stringify({ nginx, production_bundle: true, deep_link_fallback: true, missing_asset_404: true, api_proxy: true, unauthenticated_api_401: true, sign_in_screen: true, live_auth0_acceptance: false }, null, 2));
    console.log(JSON.stringify({ passed: true, production_static_smoke: true }));
  } finally { await browser.close(); }
} finally { try { docker("rm", "-f", name); } catch {} }
