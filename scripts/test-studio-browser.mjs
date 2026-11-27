import { spawn } from "node:child_process";
import { once } from "node:events";
import { existsSync } from "node:fs";
import { createServer } from "node:net";
import { resolve } from "node:path";
import { chromium } from "../apps/console/node_modules/playwright/index.mjs";

// A separate source-preview server and fixture API. Never touches running stacks.
const root = resolve(new URL("..", import.meta.url).pathname);
const libraryPath = process.env.RINGFOUR_BROWSER_LIBRARY_PATH;
if (libraryPath && existsSync(libraryPath)) {
  process.env.LD_LIBRARY_PATH = [libraryPath, process.env.LD_LIBRARY_PATH].filter(Boolean).join(":");
  console.log(`Browser libraries: ${libraryPath}`);
} else if (libraryPath) {
  console.error(`Browser library directory does not exist: ${libraryPath}`);
  process.exit(78);
}
try {
  const probe = await chromium.launch();
  await probe.close();
} catch (error) {
  console.error(`Browser prerequisites unavailable: ${error.message}`);
  process.exit(78);
}
const socket = createServer();
socket.listen(0, "127.0.0.1");
await once(socket, "listening");
const port = socket.address().port;
await new Promise((done) => socket.close(done));
const origin = `http://localhost:${port}`;
const preview = spawn(process.execPath, ["node_modules/vite/bin/vite.js", "--host", "127.0.0.1", "--port", String(port), "--strictPort"], {
  cwd: root + "/apps/console",
  env: { ...process.env, VITE_AUTH0_DOMAIN: "browser-fixture.invalid", VITE_AUTH0_CLIENT_ID: "browser-fixture", VITE_AUTH0_AUDIENCE: "https://api.ringfour.eu" },
  stdio: "inherit",
});
try {
  let ready = false;
  for (let attempt = 0; attempt < 150; attempt++) {
    if (preview.exitCode !== null) throw new Error("Preview server exited before readiness");
    try { if ((await fetch(origin, { signal: AbortSignal.timeout(1000) })).ok) { ready = true; break; } } catch { /* Preview starting. */ }
    await new Promise((done) => setTimeout(done, 200));
  }
  if (!ready) throw new Error("Preview server readiness timed out");
  const fixture = spawn(process.execPath, ["scripts/screenshot-demo.mjs"], {
    cwd: root + "/apps/console", stdio: "inherit",
    env: { ...process.env, CONSOLE_URL: origin, SCREENSHOT_DIR: process.env.APPS_ACCEPTANCE_OUTPUT ?? root + "/test-results/apps-browser" },
  });
  const [code] = await once(fixture, "exit");
  process.exitCode = code ?? 1;
} finally {
  preview.kill("SIGTERM");
  if (preview.exitCode === null) await once(preview, "exit");
}
