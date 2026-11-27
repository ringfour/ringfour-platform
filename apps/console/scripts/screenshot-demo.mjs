import { chromium } from "playwright";
import { createHash } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";

// Isolated browser fixture: no real Auth0, API, database, robot or storage writes.
// This exercises actual Studio components; it is not live/human acceptance.
const origin = process.env.CONSOLE_URL ?? "http://localhost:5173";
const output = resolve(process.env.SCREENSHOT_DIR ?? new URL("../test-results/browser", import.meta.url).pathname);
const date = new Date().toISOString();
const org = { id: "workshop", name: "Ringfour Workshop", role: "owner" };
const robots = [{ id: "virt2", organization_id: org.id, name: "virt2", appearance: "base101" }, { id: "robot-b", organization_id: org.id, name: "Robot B" }];
const camera = { id: "camera", publisher_organization_id: "official", publisher_name: "Ringfour", publisher_type: "ringfour", app_key: "camera-snapshot-loop", name: "Camera Snapshot Loop", description: "Save a camera photo every 30 seconds" };
const privateApp = { ...camera, id: "evidence", app_key: "evidence-note", name: "Evidence Note", publisher_type: "workspace", publisher_name: "Workshop tools", description: "Write a small result file for an inspection" };
const releaseDescription = "Takes a camera photo every 30 seconds and saves it in Files.";
const manifest = { schema_version: 2, app_key: camera.app_key, release: "1.0.0", display: { name: camera.name, description: releaseDescription }, capabilities: { required: [{ capability: "cameras" }], optional: [] }, targets: [{ os: "linux", architecture: "amd64", image: "registry.example/app@sha256:" + "a".repeat(64) }], configuration: { environment:{CAPTURE_INTERVAL_SECONDS:"interval_seconds"}, defaults: { interval_seconds: 30 }, schema: { properties: { interval_seconds: { type: "number", minimum: 0.5, maximum: 300 } }, required: ["interval_seconds"] } } };
manifest.runtime_settings={defaults:{capture_interval_seconds:30},schema:{properties:{capture_interval_seconds:{type:"number",minimum:0.5,maximum:300}},required:["capture_interval_seconds"]}};
let liveMode="idle";
let requestedSettings={capture_interval_seconds:3.5};
const revision = { id: "release", app_id: camera.id, publisher_organization_id: "official", manifest, visibility: "listed", manifest_digest: "sha256:" + "a".repeat(64), published_at: date };
const privateRevision = { ...revision, id: "evidence-release", app_id: privateApp.id, visibility: "unlisted", manifest: { ...manifest, app_key: privateApp.app_key, display: { name: privateApp.name, description: "Saves a short inspection note as a file." } } };
const installations = robots.map((robot) => ({ id: "install-" + robot.id, app_id: camera.id, robot_id: robot.id, organization_id: org.id, installed: { revision_id: revision.id, configuration_revision_id: "config-" + robot.id, image: manifest.targets[0].image, installed_at: date, observed_at: date }, candidate: null, removed_at: null, last_observed_at: date }));
const run = { id: "camera-run", organization_id: org.id, installation_id: installations[0].id, robot_id: "virt2", state: "succeeded", snapshot: { app_id: camera.id, revision_id: revision.id, configuration_revision_id: "config-virt2", configuration: manifest.configuration.defaults, image: manifest.targets[0].image }, requested_by: "owner", requested_at: date, observed_at: date, started_at: date, ended_at: date, completed_at: date, failure: null };
const png = await readFile(new URL("../public/app-artwork/camera-capture.png", import.meta.url));
const file = { artifact_id: "photo", run_id: run.id, installation_id: installations[0].id, robot_id: "virt2", path: "snapshot.png", content_type: "image/png", size: png.length, sha256: createHash("sha256").update(png).digest("hex"), published_at: date };
const pageOf = (items) => ({ items, next_cursor: null });
function fixture(url) {
  const path = new URL(url).pathname.replace(/^\/api/, "");
  if (path === "/health/ready") return { status:"ok", version:"browser-fixture" };
  if (path === "/v1/me") return { user: { id: "owner", name: "Operator" }, memberships: [] };
  if (path === "/v1/organizations") return pageOf([org,{id:"another-workshop",name:"Another workshop",role:"viewer"}]);
  if (path.endsWith("/robots")) return pageOf(robots);
  const robot = robots.find((item) => path === `/v1/robots/${item.id}`);
  if (robot) return robot;
  if (path.endsWith("/app-status")) return { connectivity: "online", last_seen_at: date, received_at: date, support: "supported", supports_app_uninstall: true, platform: { os: "linux", architecture: "amd64" }, inventory: { state: "available", current: { observed_at: date, capabilities: ["cameras", "imu"], instances: { cameras: ["base"] }, source: { profile: { name: "base101", model: "Simulator", version: "1" } } } } };
  if (path.endsWith("/app-compatibility")) return { status: "compatible", inventory_observed_at: date, missing_required: [], missing_optional: [], missing_native: [] };
  if (path === "/v1/store/apps") return pageOf([camera]);
  if (path.endsWith("/app-imports")) return pageOf([camera, privateApp].map((item) => ({ id: "import-" + item.id, app_id: item.id, revision_id: item.id === privateApp.id ? privateRevision.id : revision.id, imported_at: date, source: { kind: "store" } })));
  if (path === "/v1/apps/camera") return camera;
  if (path === "/v1/apps/evidence") return privateApp;
  if (path === "/v1/apps/evidence/revisions") return pageOf([privateRevision]);
  if (path === "/v1/app-revisions/evidence-release") return privateRevision;
  if (path.endsWith("/revisions")) return pageOf([revision]);
  if (path === "/v1/app-revisions/release") return revision;
  if (path.endsWith("/app-installations")) return pageOf(installations.filter((item) => path.includes(`/robots/${item.robot_id}/`)));
  if (path.endsWith("/configuration-revisions")) return pageOf(installations.map((item) => ({ id: item.installed.configuration_revision_id, revision_id: revision.id, values: manifest.configuration.defaults })));
  if (path.includes("/configuration-revisions/")) return { values: manifest.configuration.defaults };
  if (path.endsWith("/settings")) return { support:liveMode === "unsupported" ? "unsupported" : "supported", runtime_settings:liveMode === "unsupported" ? null : manifest.runtime_settings, selected_for_next_start:{id:"settings-selected",values:liveMode === "applied" ? requestedSettings : {capture_interval_seconds:30}}, active_run_id:liveMode === "idle" || liveMode === "unsupported" ? null : run.id, last_applied:liveMode === "idle" || liveMode === "unsupported" ? null : {id:"settings-confirmed",values:liveMode === "applied" ? requestedSettings : {capture_interval_seconds:30},created_at:date}, pending_application:liveMode === "pending" ? {application_id:"settings-request",run_id:run.id,values:requestedSettings,state:"pending",initial:false,delivery_state:"sent",requested_at:date,reason:null} : null };
  if (path.endsWith("/app-runs")) return pageOf([liveMode !== "idle" && liveMode !== "unsupported" ? {...run,state:"running",ended_at:null,completed_at:null} : run]);
  if (path === "/v1/app-runs/camera-run") return run;
  if (path.endsWith("/artifacts")) return pageOf([file]);
  if (path.endsWith("/output")) return { data_base64: Buffer.from("Photo published").toString("base64"), next_offset: 15, sealed: true, truncated: false };
  if (path.endsWith("/invitations")) return pageOf([]);
  if (path.endsWith("/app-logbook")) return pageOf([{event_id:"event-photo",organization_id:org.id,robot_id:"virt2",run_id:run.id,installation_id:installations[0].id,app_id:camera.id,revision_id:revision.id,operation_id:null,actor_user_id:"owner",type:"app.logbook.entry",occurred_at:date,received_at:date,payload:{message:"Photo published",path:"snapshot.png"}},...(["rejected","applied"].includes(liveMode) ? [{event_id:"settings-outcome",organization_id:org.id,robot_id:"virt2",run_id:run.id,installation_id:installations[0].id,app_id:camera.id,revision_id:revision.id,operation_id:null,actor_user_id:"owner",type:"app.settings."+liveMode,occurred_at:date,received_at:date,payload:{reason:liveMode === "rejected" ? "Capture interval refused by App" : null}}] : [])]);
  if ( path.endsWith("/interactions") || path.endsWith("/members") || path.endsWith("/credentials")) return pageOf([]);
  return undefined;
}
await mkdir(output, { recursive: true });
const browser = await chromium.launch();
const errors = [];
const results = [];
try {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
  await context.route("**/node_modules/.vite/deps/@auth0_auth0-react.js*", (route) => route.fulfill({ contentType: "text/javascript", body: 'const token=async()=>"browser-fixture";export const useAuth0=()=>({isAuthenticated:true,isLoading:false,getAccessTokenSilently:token,user:{name:"Operator"}});export const Auth0Provider=({children})=>children;' }));
  const apiRequests=[];
  await context.route("**/api/**", async (route) => {
    apiRequests.push(new URL(route.request().url()).pathname);
    if(process.env.NAV_BENCHMARK) await new Promise(resolve=>setTimeout(resolve,new URL(route.request().url()).pathname === "/api/v1/me" ? 220 : 5));
    if (route.request().url().endsWith("/artifacts/photo/content")) return route.fulfill({ body: png, contentType: "image/png" });
    if (route.request().method() === "POST" && new URL(route.request().url()).pathname.endsWith("/settings")) { requestedSettings=route.request().postDataJSON().values; liveMode="pending"; return route.fulfill({status:202,json:{state:"pending",application_id:"settings-request",run_id:run.id}}); }
    const value = fixture(route.request().url());
    if (value === undefined) { errors.push(`Missing fixture ${new URL(route.request().url()).pathname}`); return route.fulfill({ status: 404, json: { error: { message: "Fixture missing" } } }); }
    return route.fulfill({ json: value });
  });
  const tab = await context.newPage();
  tab.on("pageerror", (error) => errors.push(error.message));
  if(process.env.NAV_BENCHMARK) {
    const samples=[];
    const measure=async(name,action,ready)=>{
      const count=apiRequests.length;
      const start=performance.now();
      await action(); await ready();
      await tab.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
      samples.push({name,ms:Math.round((performance.now()-start)*10)/10,requests:apiRequests.length-count});
    };
    const rail=tab.getByRole("navigation", {name:"Main navigation",exact:true});
    await measure("cold Home",()=>tab.goto(origin+"/overview?workspace=workshop"),()=>tab.getByRole("link",{name:"virt2",exact:true}).waitFor());
    let documents=0; tab.on("request",request=>{if(request.isNavigationRequest() && request.frame()===tab.mainFrame())documents++;});
    for(let repeat=0;repeat<5;repeat++) {
      await measure("Home to Robots",()=>rail.getByRole("link",{name:"Robots",exact:true}).click(),()=>tab.getByRole("link",{name:"virt2",exact:true}).waitFor());
      await measure("Robots to Home",()=>rail.getByRole("link",{name:"Home",exact:true}).click(),()=>tab.getByRole("link",{name:"virt2",exact:true}).waitFor());
    }
    await rail.getByRole("link",{name:"Apps",exact:true}).click();
    await tab.locator('a[href^="/apps/camera/overview"]').first().click();
    await tab.getByRole("combobox",{name:"Robot",exact:true}).selectOption("install-virt2");
    await tab.getByRole("heading",{name:"Not running",exact:true}).waitFor();
    for(let repeat=0;repeat<5;repeat++) {
      await measure("Overview to Settings",()=>tab.getByRole("link",{name:"Settings",exact:true}).click(),()=>tab.getByRole("button",{name:"Edit startup configuration",exact:true}).waitFor());
      await measure("Settings to Overview",()=>tab.getByRole("link",{name:"Overview",exact:true}).click(),()=>tab.getByRole("heading",{name:"Not running",exact:true}).waitFor());
    }
    const summary={};
    for(const name of [...new Set(samples.map(item=>item.name))]) {
      const subset=samples.filter(item=>item.name===name),times=subset.map(item=>item.ms).sort((a,b)=>a-b);
      summary[name]={p50_ms:times[Math.floor(times.length/2)],max_ms:times.at(-1),requests:subset.map(item=>item.requests)};
    }
    const report={fixture:true,origin,api_latency_ms:5,profile_latency_ms:220,samples,summary,document_reloads_during_navigation:documents};
    await writeFile(resolve(output,process.env.BENCHMARK_FILE ?? "navigation-benchmark.json"),JSON.stringify(report,null,2));
    console.log(JSON.stringify(report));
  } else {
  for (const [name, path] of [["home", "/overview"], ["store", "/app-store"], ["listing", "/app-store/camera?robot=virt2"], ["apps", "/apps"], ["enrollment", "/robots/enroll"], ["robots", "/robots"], ["robot", "/robots/virt2"], ["credentials", "/robots/virt2/credentials"], ["app", "/apps/camera/overview?installation=install-virt2"], ["settings", "/apps/camera/settings?installation=install-robot-b"], ["activity", "/apps/camera/activity?installation=install-virt2"], ["run", "/runs/camera-run"], ["files", "/apps/camera/files?installation=install-virt2"], ["logbook", "/runs"], ["timeline", "/timeline"], ["run-output", "/runs/camera-run?tab=Output"], ["organizations", "/settings/workspace"], ["health", "/settings/health"], ["profile", "/settings/profile"]]) {
    await tab.goto(origin + path + (path.includes("?") ? "&" : "?") + "workspace=workshop");
    await tab.waitForLoadState("networkidle");
    if (name === "run") {
      await tab.getByRole("region", {name:"Run artifacts"}).scrollIntoViewIfNeeded();
      await tab.getByRole("img", {name:"Published photo: snapshot.png"}).waitFor();
      await tab.evaluate(()=>window.scrollTo(0,0));
    }
    if (errors.length) throw new Error(errors.join("\n"));
    const checkChrome = async () => {
      const toolbar = tab.getByRole("navigation", {name:"Studio toolbar",exact:true});
      const header = tab.locator("main .studio-page-header");
      if (await toolbar.count() !== 1 || await header.count() !== 1 || await header.getByRole("heading", {level:1}).count() !== 1) throw new Error(`Missing shared page chrome: ${name}`);
      const content = await tab.locator(".studio-page-content").boundingBox(), bounds = await header.boundingBox();
      if (!content || !bounds || Math.abs(bounds.x - content.x) > 1 || Math.abs(bounds.width - content.width) > 1 || Math.abs(bounds.y - content.y) > 1) throw new Error(`Header is inset from content: ${name}`);
      const size = await header.getByRole("heading",{level:1}).evaluate(node=>parseFloat(getComputedStyle(node).fontSize));
      const expected = Math.max(32,Math.min(await tab.evaluate(()=>innerWidth)*0.03,44));
      if (Math.abs(size - expected) > 0.1) throw new Error(`Header type differs from Apps: ${name}`);

    };
    await checkChrome();
    if (name === "app") {
      const capture = tab.getByRole("region", { name: "Latest capture" });
      await capture.scrollIntoViewIfNeeded();
      await capture.getByRole("img", { name: "Published photo: snapshot.png" }).waitFor();
      await tab.evaluate(() => window.scrollTo(0, 0));
      const header = tab.locator(".studio-resource-header");
      if (await header.getByRole("heading", { name: camera.name, exact: true }).count() !== 1) throw new Error("Shared App header lost its identity");
      if (await header.getByText("Current execution", { exact: true }).count() !== 1 || await header.getByRole("button", { name: "Start on virt2" }).count() !== 1) throw new Error("Execution controls left the App header");
      if (!await header.getByRole("img", { name: /seen from the simulated robot/ }).isVisible()) throw new Error("Installed App header is missing its image");
      if (await header.getByText(releaseDescription, { exact: true }).count() !== 1) throw new Error("App description missing from header");
      if (await tab.getByRole("main").getByRole("navigation", { name: "App sections" }).count() !== 1 || await header.getByLabel("Robot", {exact:true}).inputValue() !== "install-virt2") throw new Error("App header lost tabs or exact robot target");
    }
    if (["app","settings","activity","files","robot"].includes(name)) {
      for (const section of await tab.locator(".studio-content-grid .studio-section").all()) {
        const heading = section.locator(".studio-section-heading h2").first();
        if (!await heading.count()) continue;
        const bounds = await section.boundingBox(), title = await heading.boundingBox();
        if (!bounds || !title || title.x - bounds.x < 20 || title.x + title.width > bounds.x + bounds.width) throw new Error(`Card heading lacks side padding: ${name} ${JSON.stringify({bounds,title,heading:await heading.innerText(),classes:await section.getAttribute("class")})}`);
      }
      if(name === "settings") {
        const forms=await tab.locator('.studio-section[data-width="form"]').all();
        if(forms.length !== 2 || Math.abs((await forms[0].boundingBox()).width-(await forms[1].boundingBox()).width)>1) throw new Error("Startup and live form widths differ");
      }
    }
    if (name === "robots") {
      const edge = (await tab.locator(".studio-page-heading-copy").boundingBox()).x;
      const cards = tab.locator(".robot-card");
      if (await cards.count() !== robots.length) throw new Error("Robot cards missing");
      const first = await cards.first().boundingBox();
      if (!first || Math.abs(first.x - edge) > 1) throw new Error("Robot cards are inset from filters and heading");
      if (!await cards.first().locator(".robot-card-image").isVisible()) throw new Error("Robot image is missing from its card");
    }
    if (name === "store") {
      const cards = tab.locator(".store-listing-card");
      if (await cards.count() !== 2) throw new Error("Store should show a full card per App");
      const evidence = tab.getByRole("link", { name: "View Evidence Note" });
      if (!(await evidence.getAttribute("href"))?.includes("release=evidence-release") || !await evidence.getByText(privateRevision.manifest.display.description, { exact: true }).isVisible()) throw new Error("Private card lost its imported release description");
      for (const card of await cards.all()) {
        const headerImage = card.locator("img.store-header-image");
        await headerImage.scrollIntoViewIfNeeded();
        await headerImage.evaluate((img) => img.decode());
      }
      await tab.evaluate(() => window.scrollTo(0, 0));
    }
    if (name === "listing") {
      if (!await tab.getByText(releaseDescription, { exact: true }).isVisible()) throw new Error("Store listing lost the App description");
      if (!await tab.getByRole("img", { name: /seen from the simulated robot/ }).isVisible()) throw new Error("Store listing lost its simulator capture");
      if (!await tab.getByText("Works with virt2 · uses its base camera", { exact: true }).isVisible()) throw new Error("Store listing lost robot-specific compatibility");
      if (!await tab.getByRole("link", { name: "Open on virt2" }).isVisible()) throw new Error("Store listing lost its exact installed target");
    }
    if (name === "robot") {
      const apps = await tab.getByRole("region", { name: "Installed Apps" }).boundingBox();
      const details = await tab.locator(".studio-page-header").getByRole("region", { name: "Device details" }).boundingBox();
      const caps = await tab.getByRole("region", { name: "Capabilities" }).boundingBox();
      const runs = await tab.getByRole("region", { name: "Recent App runs" }).boundingBox();
      if (!apps || !details || !caps || !runs || details.y + details.height > apps.y || caps.x <= apps.x || Math.abs(caps.y - apps.y) > 1 || runs.y < Math.max(caps.y + caps.height, apps.y + apps.height) || Math.abs(runs.x - apps.x) > 1 || Math.abs(runs.x + runs.width - caps.x - caps.width) > 1) throw new Error("Robot detail columns or full-width runs are misaligned");
    }
    if (await tab.locator(".studio-shell").evaluate((node) => getComputedStyle(node).backgroundColor) !== "rgb(8, 8, 8)") throw new Error(`Wrong workspace palette: ${name}`);
    const navigation = tab.getByRole("navigation", {name:"Main navigation",exact:true});
    const rail = tab.locator(".studio-sidebar");
    if (Math.abs((await rail.boundingBox()).width - 72) > 1) throw new Error("Sidebar is not an icon rail");
    if (await rail.getByRole("combobox").count()) throw new Error("Workspace picker remains in the sidebar");
    const account = tab.getByRole("navigation",{name:"Studio toolbar"});
    if (await account.count() !== 1) throw new Error("Top-bar account links missing");
    if (name === "profile" && await tab.getByRole("main").getByRole("combobox",{name:"Workspace",exact:true}).inputValue() !== org.id) throw new Error("Profile workspace selection is incorrect");
    if (name === "logbook") {
      const groups = tab.getByRole("main").getByRole("navigation", {name:"Run groups",exact:true});
      if (await groups.count() !== 1) throw new Error("Logbook groups are not in the results card");
      const filters = await tab.getByRole("region", {name:"Run filters",exact:true}).boundingBox();
      const groupBounds = await groups.boundingBox();
      if (!filters || !groupBounds || groupBounds.y < filters.y + filters.height) throw new Error("Run groups are above filters");
    }
    const runTabs = ["run", "run-output"].includes(name) ? "Run sections" : undefined;
    if (runTabs && await tab.getByRole("main").getByRole("navigation", {name:runTabs,exact:true}).count() !== 1) throw new Error("Logbook tabs are missing from the page");
    for (const label of ["Organizations","System health","My profile"]) if (await account.getByRole("link",{name:label,exact:true}).count() !== 1) throw new Error("Top-bar account link missing");
    if (await tab.locator("body").evaluate((node) => node.scrollWidth > innerWidth)) throw new Error(`Horizontal overflow: ${name}`);
    await tab.screenshot({ path: resolve(output, `${name}-desktop.png`), fullPage: true });
    await tab.setViewportSize({ width: 390, height: 844 });
    await checkChrome();
    await tab.screenshot({ path: resolve(output, `${name}-narrow.png`), fullPage: true });
    if (await tab.locator("body").evaluate((node) => node.scrollWidth > innerWidth)) throw new Error(`Narrow horizontal overflow: ${name}`);
    await tab.setViewportSize({ width: 1440, height: 1000 });
    results.push(name);
  }
  // Editorial review fixtures are scoped to this browser and removed before the other journeys.
  const twins = ["Harbor Lab", "Workshop tools"].map((publisher_name, index) => ({
    ...privateApp, id: "check-" + index, app_key: "robot-check-" + index,
    name: "Robot Check", description: "Check the robot sensors", publisher_name,
  }));
  let reviewCatalog = [camera];
  await tab.route("**/api/v1/store/apps?**", route => route.fulfill({json:pageOf(reviewCatalog)}));
  await tab.route("**/app-imports*", route => route.fulfill({json:pageOf([])}));
  for (const [name, catalog, query, count] of [
    ["one", [camera], "", 1],
    ["four", [camera, privateApp, ...twins], "", 4],
    ["similar", [camera, privateApp, ...twins], "&q=Robot%20Check", 2],
  ]) {
    reviewCatalog = catalog;
    await tab.goto(origin + "/app-store?workspace=workshop" + query);
    await tab.locator(".store-listing-card").first().waitFor();
    if (await tab.locator(".store-listing-card").count() !== count) throw new Error("Unexpected catalog size: " + name);
    if (query && await tab.locator("[data-featured]").count()) throw new Error("Feature duplicated in search");
    for (const width of [1440,390]) {
      await tab.setViewportSize({width,height:900});
      await tab.screenshot({path:resolve(output,`store-${name}-${width}.png`),fullPage:true});
    }
  }
  await tab.unroute("**/api/v1/store/apps?**");
  await tab.unroute("**/app-imports*");
  await tab.goto(origin + "/app-store?workspace=workshop&robot=virt2&q=Evidence");
  await tab.getByRole("link",{name:"View Evidence Note"}).click();
  await tab.getByRole("heading",{name:"Release",exact:true}).waitFor();
  if (await tab.locator("figure.store-capture").count()) throw new Error("Private App displayed an unrelated preview");
  await tab.goBack();
  const restored = tab.getByRole("link",{name:"View Evidence Note"});
  await restored.waitFor();
  if (!tab.url().includes("robot=virt2&q=Evidence") || !await restored.evaluate(node => node === document.activeElement)) throw new Error("Back lost catalog context or focus");
  const longTitle = "Inspection evidence from the cameras on the warehouse simulator during an overnight survey";
  await tab.route("**/apps/evidence/revisions*", route => route.fulfill({json:pageOf([{...privateRevision,manifest:{...privateRevision.manifest,display:{...privateRevision.manifest.display,name:longTitle}}}])}));
  await tab.route("**/app-compatibility?**", route => route.fulfill({json:{status:"missing_required",missing_required:[{capability:"cameras"}],missing_optional:[],missing_native:[],inventory_observed_at:date}}));
  await tab.goto(origin + "/app-store/evidence?workspace=workshop&release=evidence-release&robot=virt2");
  await tab.getByRole("heading",{name:longTitle}).waitFor();
  await tab.getByText("virt2 does not report this capability").waitFor();
  if (await tab.getByRole("button",{name:"Install on virt2",exact:true}).isEnabled()) throw new Error("Incompatible installation enabled");
  for (const width of [1440,390]) {
    await tab.setViewportSize({width,height:900});
    if(await tab.locator("body").evaluate(node=>node.scrollWidth>innerWidth)) throw new Error("Long listing overflows");
    await tab.screenshot({path:resolve(output,`listing-incompatible-${width}.png`),fullPage:true});
  }
  await tab.unroute("**/apps/evidence/revisions*");
  await tab.unroute("**/app-compatibility?**");
  await tab.setViewportSize({ width: 768, height: 1024 });
  await tab.goto(origin + "/apps/camera/settings?workspace=workshop&installation=install-virt2");
  await tab.waitForLoadState("networkidle");
  if (await tab.locator("body").evaluate(node=>node.scrollWidth>innerWidth)) throw new Error("Tablet top bar overflows");
  await tab.screenshot({path:resolve(output,"toolbar-tablet.png"),fullPage:true});
  await tab.setViewportSize({ width: 1920, height: 1080 });
  await tab.goto(origin + "/runs?workspace=workshop");
  await tab.getByRole("heading", {name:"Logbook",exact:true}).waitFor();
  const main = await tab.locator("main").boundingBox();
  if (!main || main.width < 1600) throw new Error("Logbook wastes available desktop width");
  await tab.screenshot({path:resolve(output,"logbook-wide.png"),fullPage:true});
  await tab.setViewportSize({ width: 390, height: 844 });
  await tab.getByRole("button", { name: "Open navigation", exact: true }).click();
  const navigation = tab.getByRole("dialog", { name: "Navigation", exact: true });
  await navigation.waitFor();
  await tab.screenshot({ path: resolve(output, "navigation-narrow.png") });
  await tab.keyboard.press("Tab");
  if (!await navigation.evaluate((node) => node.contains(document.activeElement))) throw new Error("Navigation drawer focus escaped");
  await tab.keyboard.press("Escape"); await navigation.waitFor({ state: "detached" });
  if (await tab.locator("body").evaluate(() => document.activeElement?.getAttribute("aria-label")) !== "Open navigation") throw new Error("Navigation drawer did not return focus");
  await tab.setViewportSize({ width: 1440, height: 1000 });
  await tab.goto(origin + "/runs?workspace=workshop&group=Finished");
  const runTitle = tab.getByRole("link", { name: "Camera Snapshot Loop", exact: true });
  await runTitle.waitFor();
  const openRun = await tab.getByText("Open run →", { exact: true }).boundingBox();
  if (!openRun) throw new Error("Run row is missing its open affordance");
  await tab.mouse.click(openRun.x + openRun.width / 2, openRun.y + openRun.height / 2);
  await tab.getByText("App run record",{exact:true}).waitFor();
  await tab.getByRole("link",{name:"Output",exact:true}).click();
  await tab.getByRole("heading",{name:"Standard output",exact:true}).waitFor();
  await tab.getByRole("heading",{name:"Standard error",exact:true}).waitFor();
  await tab.getByRole("link",{name:/Back to runs/}).click();
  await tab.waitForURL("**/runs?workspace=workshop&group=Finished");
  await tab.getByRole("combobox",{name:"Status",exact:true}).selectOption("succeeded");
  if (!tab.url().includes("state=succeeded")) throw new Error("Logbook filters were not retained in the URL");
  await tab.goto(origin + "/app-store/camera?workspace=workshop&robot=robot-b&install=1");
  const dialog = tab.getByRole("dialog"); await dialog.waitFor();
  if (!(await dialog.getByRole("link", { name: "Open on Robot B" }).getAttribute("href"))?.includes("install-robot-b")) throw new Error("Existing installation lost Robot B context");
  await tab.keyboard.press("Tab");
  if (!await dialog.evaluate((node) => node.contains(document.activeElement))) throw new Error("Dialog focus escaped");
  await tab.keyboard.press("Escape"); await dialog.waitFor({ state: "detached" });
  await tab.goto(origin + "/apps/camera/files?workspace=workshop&installation=install-virt2");
  await tab.getByRole("button", { name: "Preview", exact: true }).click();
  await tab.getByRole("img", { name: "Published result: snapshot.png" }).waitFor();
  await tab.keyboard.press("Escape");
  await tab.goto(origin + "/robots/virt2?workspace=workshop");
  await tab.getByRole("link", { name: "Device access" }).click();
  await tab.waitForURL("**/robots/virt2/credentials?workspace=workshop");
  await tab.goBack(); await tab.waitForURL("**/robots/virt2?workspace=workshop");
  await tab.reload(); await tab.getByRole("heading", { name: "virt2", exact: true }).waitFor();
  await tab.getByRole("navigation", { name: "Studio toolbar" }).getByRole("link", { name: "My profile" }).click();
  await tab.getByRole("link", { name: "Back to robot", exact: true }).click();
  await tab.waitForURL("**/robots/virt2?workspace=workshop");
  await tab.goto(origin + "/apps/camera/overview?workspace=workshop&installation=install-robot-b");
  await tab.getByRole("link", { name: "Settings", exact: true }).click();
  await tab.getByRole("button", { name: "Edit startup configuration" }).click();
  await tab.getByLabel("interval seconds", { exact: true }).fill("2.5");
  tab.once("dialog", (dialog) => dialog.dismiss());
  await tab.getByRole("navigation",{name:"Studio toolbar"}).getByRole("link",{name:"My profile"}).click();
  if (!tab.url().includes("/settings?") || await tab.getByLabel("interval seconds",{exact:true}).inputValue() !== "2.5") throw new Error("Rejected profile navigation lost context or edits");
  tab.once("dialog", (dialog) => dialog.dismiss());
  await tab.getByRole("main").getByRole("link",{name:"Back to Apps"}).click();
  if (!tab.url().includes("/settings") || await tab.getByLabel("interval seconds",{exact:true}).inputValue() !== "2.5") throw new Error("Top-bar back navigation discarded dirty edits");
  tab.once("dialog", (dialog) => dialog.dismiss());
  await tab.getByRole("link", { name: "Files", exact: true }).click();
  if (!tab.url().includes("/settings")) throw new Error("Rejected dirty navigation changed the route");
  if (await tab.getByLabel("interval seconds", { exact: true }).inputValue() !== "2.5") throw new Error("Rejected navigation lost edits");
  tab.once("dialog", (dialog) => dialog.dismiss());
  await tab.evaluate(() => history.back());
  await tab.waitForTimeout(200);
  if (!tab.url().includes("/settings") || await tab.getByLabel("interval seconds", { exact: true }).inputValue() !== "2.5") throw new Error("Rejected browser Back lost route or edits");
  tab.once("dialog", (dialog) => dialog.accept());
  await tab.getByRole("link", { name: "Files", exact: true }).click();
  await tab.waitForURL("**/files?workspace=workshop&installation=install-robot-b");
  await tab.goBack();
  await tab.getByRole("button", { name: "Edit startup configuration" }).click();
  await tab.getByLabel("interval seconds", { exact: true }).fill("2.5");
  tab.once("dialog", (dialog) => dialog.dismiss());
  await tab.evaluate(() => history.forward());
  await tab.waitForTimeout(200);
  if (!tab.url().includes("/settings")) throw new Error("Rejected browser Forward changed the route");
  tab.once("dialog", (dialog) => dialog.accept());
  await tab.getByRole("link", { name: "Files", exact: true }).click();
  await tab.getByRole("navigation",{name:"Studio toolbar"}).getByRole("link",{name:"My profile"}).click();
  const workspacePicker = tab.getByRole("main").getByRole("combobox",{name:"Workspace",exact:true});
  await workspacePicker.selectOption("another-workshop");
  await tab.waitForURL("**/settings/profile?workspace=another-workshop");
  if (await workspacePicker.inputValue() !== "another-workshop") throw new Error("Profile did not retain the new workspace");
  await workspacePicker.selectOption("workshop");
  await tab.waitForURL("**/settings/profile?workspace=workshop");
  await tab.goto(origin + "/logbook/camera-run?workspace=workshop#output");
  await tab.waitForURL("**/runs/camera-run?workspace=workshop#output");
  liveMode="running";
  await tab.goto(origin+"/apps/camera/settings?workspace=workshop&installation=install-virt2");
  const liveSection=tab.getByRole("region",{name:"Live settings",exact:true});
  await liveSection.getByLabel("capture interval seconds · required",{exact:true}).fill("3.5");
  await liveSection.getByRole("button",{name:"Apply live settings",exact:true}).click();
  await liveSection.getByText("Requested: 3.5 · Awaiting App confirmation",{exact:true}).waitFor();
  await liveSection.getByText("Confirmed: 30",{exact:true}).waitFor();
  await tab.screenshot({path:resolve(output,"settings-pending-desktop.png"),fullPage:true});
  liveMode="rejected";
  await liveSection.getByRole("button",{name:"Refresh status",exact:true}).click();
  await liveSection.getByText("Last settings request rejected: Capture interval refused by App",{exact:true}).waitFor();
  await liveSection.getByText("Confirmed: 30",{exact:true}).waitFor();
  await tab.screenshot({path:resolve(output,"settings-rejected-desktop.png"),fullPage:true});
  await liveSection.getByLabel("capture interval seconds · required",{exact:true}).fill("4.5");
  await liveSection.getByRole("button",{name:"Apply live settings",exact:true}).click();
  await liveSection.getByText("Requested: 4.5 · Awaiting App confirmation",{exact:true}).waitFor();
  liveMode="applied";
  await liveSection.getByRole("button",{name:"Refresh status",exact:true}).click();
  await liveSection.getByText("Confirmed: 4.5",{exact:true}).waitFor();
  await tab.screenshot({path:resolve(output,"settings-confirmed-desktop.png"),fullPage:true});
  liveMode="unsupported";
  await tab.reload();
  await liveSection.getByText("This App release does not declare live settings. Startup configuration remains available above.",{exact:true}).waitFor();
  await tab.screenshot({path:resolve(output,"settings-unsupported-desktop.png"),fullPage:true});
  if (errors.length) throw new Error(errors.join("\n"));
  await writeFile(resolve(output, "results.json"), JSON.stringify({ fixture: true, live_acceptance: false, screenshots: results, store_one_four_similar: true, store_filtered_back_focus: true, robot_header_device_details: true, native_modal_keyboard: true, mobile_navigation_keyboard: true, shared_app_header: true, shared_page_headers: true, shared_content_grid: true, section_cards: true, aligned_form_widths: true, live_settings_feedback: true, full_width_chrome: true, page_heading_back: true, restored_logbook: true, logbook_output_and_filter_back: true, neutral_palette: true, icon_only_sidebar: true, inline_logbook_groups: true, profile_workspace_picker: true, inline_page_tabs: true, profile_navigation_dirty_guard: true, topbar_organization_picker: true, wide_logbook: true, dirty_link_back_forward_guards: true, verified_image_preview: true, robot_credentials_back_refresh: true, alias_normalization: true }, null, 2));
  console.log(JSON.stringify({ passed: true, fixture: true, screenshots: results.length * 2, output }));
  }
} finally { await browser.close(); }
