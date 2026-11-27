import { chromium } from "playwright";
import { mkdir } from "node:fs/promises";
import { resolve } from "node:path";

// Browser-only sample data. No API or database records are created.
const origin = process.env.CONSOLE_URL ?? "http://localhost:5173";
const output = resolve(process.env.SCREENSHOT_DIR ?? "../../screenshots/demo");
const width = Number(process.env.SCREENSHOT_WIDTH ?? 1600);
if (!Number.isInteger(width) || width < 320) throw new Error("SCREENSHOT_WIDTH must be at least 320 pixels");
const minutesAgo = (minutes) => new Date(Date.now() - minutes * 60_000).toISOString();
const org = { id: "sample-workspace", name: "Atlas Demo", role: "owner", created_at: minutesAgo(40000) };
const people = [
  { user_id: "sample-operator", name: "Maya Chen", role: "owner" },
  { user_id: "sample-engineer", name: "Noah Patel", role: "developer" },
  { user_id: "sample-analyst", name: "Elena Ruiz", role: "operator" },
];
const members = people.map((person, index) => ({
  ...person, organization_id: org.id, created_at: minutesAgo(35000 - index * 1000),
  email: `${person.name.toLowerCase().replace(" ", ".")}@example.com`,
  profile_picture_url: null,
}));
const robots = [
  { id: "sample-rover", name: "Field rover 01", organization_id: org.id },
  { id: "sample-inspector", name: "Inspection arm 02", organization_id: org.id },
  { id: "sample-scout", name: "Survey scout 03", organization_id: org.id },
  { id: "sample-dock", name: "Dock station 04", organization_id: org.id },
];
const apps = [
  { id: "sample-mapping", name: "Site mapping", latest_version: 4, created_by_user_id: people[1].user_id },
  { id: "sample-inspection", name: "Visual inspection", latest_version: 3, created_by_user_id: people[1].user_id },
  { id: "sample-inventory", name: "Asset inventory", latest_version: 2, created_by_user_id: people[2].user_id },
  { id: "sample-diagnostics", name: "Robot diagnostics", latest_version: 1, created_by_user_id: people[0].user_id },
];
const revisions = Object.fromEntries(apps.map((app) => [app.id, [{
  id: `${app.id}-v${app.latest_version}`,
  version: app.latest_version,
  created_at: minutesAgo(1440),
  script_revision_id: `${app.id}-script`,
  manifest: { entrypoint: "main.py", python_requirement: ">=3.12", source_digest: "sample", lock_file: "requirements.lock" },
}]]));
const revision = (app) => revisions[app.id][0].id;
const run = (id, robot, app, state, actor, started, updated) => ({
  id, robot_id: robot.id, mission_revision_id: revision(app), state,
  actor_user_id: actor.user_id, created_at: minutesAgo(started), updated_at: minutesAgo(updated),
});
const runs = [
  run("sample-run-map", robots[0], apps[0], "running", people[0], 41, 2),
  run("sample-run-inspect", robots[1], apps[1], "running", people[1], 23, 1),
  run("sample-run-inventory", robots[2], apps[2], "succeeded", people[2], 122, 66),
  run("sample-run-diagnostic", robots[3], apps[3], "failed", people[0], 38, 12),
  run("sample-run-scan", robots[0], apps[0], "succeeded", people[1], 330, 270),
  run("sample-run-rover-check", robots[0], apps[3], "succeeded", people[1], 290, 250),
  run("sample-run-cancel", robots[2], apps[2], "cancelled", people[0], 580, 560),
];
const deployments = [
  { id: "sample-deploy-map", robot_id: robots[0].id, selected_revision_id: revision(apps[0]), desired_state: "running", observed_state: "running", last_execution_id: runs[0].id },
  { id: "sample-deploy-inspect", robot_id: robots[1].id, selected_revision_id: revision(apps[1]), desired_state: "running", observed_state: "running", last_execution_id: runs[1].id },
  { id: "sample-deploy-inventory", robot_id: robots[2].id, selected_revision_id: revision(apps[2]), desired_state: "stopped", observed_state: "succeeded", last_execution_id: runs[2].id },
  { id: "sample-deploy-diagnostic", robot_id: robots[3].id, selected_revision_id: revision(apps[3]), desired_state: "running", observed_state: "failed", last_execution_id: runs[3].id, last_error: { code: "health_check_failed", message: "Camera feed did not pass its health check" } },
].map((item) => ({ candidate_revision_id: null, last_operation_id: null, last_error: null, created_at: minutesAgo(1440), updated_at: minutesAgo(12), ...item }));
const interactions = [{
  request_id: "sample-decision", execution_id: runs[1].id, robot_id: robots[1].id,
  created_at: minutesAgo(9), deadline: new Date(Date.now() + 20 * 60_000).toISOString(),
  prompt: "Approve inspection of the west aisle?",
  choices: [{ value: "approve", label: "Approve" }, { value: "skip", label: "Skip" }],
  state: "pending", response: null,
}];
const activity = new Map([
  [runs[0].id, "Mapped 18 of 24 zones · 75% complete"],
  [runs[1].id, "Awaiting approval for the west aisle"],
  [runs[2].id, "128 assets verified across four zones"],
  [runs[3].id, "Camera health check failed"],
  [runs[4].id, "Site map published with 24 zones"],
  [runs[5].id, "Rover sensor checks completed"],
]);
const events = runs.flatMap((item) => activity.has(item.id) ? [{
  event_id: `${item.id}-event`, robot_id: item.robot_id, operation_id: null,
  execution_id: item.id, actor_user_id: item.actor_user_id, local_sequence: 1,
  type: "mission.logbook.entry", occurred_at: item.updated_at, received_at: item.updated_at,
  payload: { message: activity.get(item.id) },
}] : []);
events.push(
  { event_id: "sample-event-map-evidence", robot_id: robots[0].id, operation_id: null,
    execution_id: runs[0].id, actor_user_id: people[0].user_id, local_sequence: 2,
    type: "mission.logbook.entry", occurred_at: minutesAgo(18), received_at: minutesAgo(17),
    payload: { level: "info", message: "Map preview saved for review", fields: {}, evidence: ["00000000-0000-4000-8000-000000000001"] } },
  { event_id: "sample-event-approval", robot_id: robots[1].id, operation_id: null,
    execution_id: runs[1].id, actor_user_id: people[1].user_id, local_sequence: 2,
    type: "human.interaction.requested", occurred_at: minutesAgo(9), received_at: minutesAgo(8),
    payload: { message: "Approval requested for the west aisle" } },
  { event_id: "sample-event-inventory-evidence", robot_id: robots[2].id, operation_id: null,
    execution_id: runs[2].id, actor_user_id: people[2].user_id, local_sequence: 2,
    type: "mission.logbook.entry", occurred_at: minutesAgo(80), received_at: minutesAgo(79),
    payload: { level: "info", message: "Asset inventory report attached", fields: {}, evidence: ["00000000-0000-4000-8000-000000000002"] } },
  { event_id: "sample-event-run-failure", robot_id: robots[3].id, operation_id: null,
    execution_id: runs[3].id, actor_user_id: null, local_sequence: 2,
    type: "mission.failed", occurred_at: minutesAgo(12), received_at: minutesAgo(12),
    payload: { message: "Camera health check failed" } },
);
const page = (items) => ({ items, next_cursor: null });

function response(path) {
  const url = new URL(path, origin);
  const parts = url.pathname.split("/").filter(Boolean);
  if (parts[0] !== "api" || parts[1] !== "v1") return null;
  if (parts[2] === "me") return {
    user: { id: people[0].user_id, name: people[0].name, email: members[0].email, created_at: members[0].created_at, profile_picture_url: null, phone_number: null },
    memberships: members,
  };
  if (parts[2] === "organizations" && parts.length === 3) return page([org]);
  if (parts[2] === "organizations" && parts[3] === org.id) {
    if (parts[4] === "robots") return page(robots);
    if (parts[4] === "missions") return page(apps);
    if (parts[4] === "members") return page(members);
    if (parts[4] === "invitations") return page([]);
    if (parts[4] === "deployments") return page(deployments);
    if (parts[4] === "interactions") return page(interactions);
    if (parts[4] === "events") {
      const id = url.searchParams.get("execution_id");
      return page(id ? events.filter((event) => event.execution_id === id) : events);
    }
  }
  if (parts[2] === "missions" && parts[4] === "revisions") return page(revisions[parts[3]] ?? []);
  if (parts[2] === "robots") {
    const robot = robots.find((item) => item.id === parts[3]);
    if (!robot) return null;
    if (!parts[4]) return robot;
    if (parts[4] === "executions") return page(runs.filter((item) => item.robot_id === robot.id));
    if (parts[4] === "status") return {
      online: robot.id !== "sample-dock", last_seen_at: minutesAgo(robot.id === "sample-dock" ? 45 : 1),
      daemon_version: "0.1.0", protocol_version: 1,
    };
  }
  if (parts[2] === "executions") {
    const item = runs.find((run) => run.id === parts[3]);
    if (!item) return null;
    if (!parts[4]) return item;
    if (parts[4] === "artifacts") return { artifacts: item.state === "succeeded" ? [{ id: `${item.id}-evidence` }] : [] };
    if (parts[4] === "output") return { data_base64: "", next_offset: 0, truncated: false };
  }
  return null;
}

await mkdir(output, { recursive: true });
const browser = await chromium.launch();
try {
  const context = await browser.newContext({ viewport: { width, height: 1000 }, deviceScaleFactor: 1 });
  const fixtureErrors = [];
  await context.route("**/node_modules/.vite/deps/@auth0_auth0-react.js*", (route) => route.fulfill({
    contentType: "text/javascript",
    body: `const getAccessTokenSilently=async()=>"sample-token"; export const useAuth0=()=>({isAuthenticated:true,isLoading:false,getAccessTokenSilently,user:{name:"Maya Chen"}}); export const Auth0Provider=({children})=>children;`,
  }));
  await context.route("**/api/**", (route) => {
    const body = response(route.request().url());
    if (body === null) {
      fixtureErrors.push(`Missing sample API fixture: ${route.request().url()}`);
      return route.fulfill({ status: 404, json: { error: { message: "Sample fixture missing" } } });
    }
    return route.fulfill({ json: body });
  });
  const tab = await context.newPage();
  tab.on("pageerror", (error) => fixtureErrors.push(error.message));
  for (const [section, route] of [
    ["overview", "overview"], ["runs", "runs"], ["robots", "robots"],
    ["apps", "apps"], ["timeline", "timeline"], ["people", "settings/workspace"],
  ]) {
    await tab.goto(`${origin}/${route}?workspace=${org.id}`);
    await tab.waitForLoadState("networkidle");
    if (fixtureErrors.length) throw new Error(fixtureErrors.join("\n"));
    if (!(await tab.getByText(org.name, { exact: true }).count()))
      throw new Error(`Sample workspace did not load on ${section}`);
    if (section === "timeline") {
      const toolbar = tab.locator("#console-timeline-toolbar");
      if (!(await toolbar.getByRole("button", { name: "Today" }).count()))
        throw new Error("Timeline toolbar did not mount below the breadcrumb");
      await toolbar.locator("summary").click();
      if (!(await toolbar.locator("form").isVisible()))
        throw new Error("Custom time range did not open");
      await toolbar.locator("summary").click();
    }
    const documentWidth = await tab.evaluate(() => document.documentElement.scrollWidth);
    if (documentWidth > width) throw new Error(`${section} overflows: ${documentWidth}px at ${width}px`);
    await tab.screenshot({ path: resolve(output, `${section}.png`), fullPage: true });
    console.log(resolve(output, `${section}.png`));
    if (section === "timeline") {
      await tab.getByRole("button", { name: /Visual inspection v3 · running/ }).hover();
      await tab.getByRole("tooltip").waitFor();
      await tab.screenshot({ path: resolve(output, "timeline-tooltip.png"), fullPage: true });
      console.log(resolve(output, "timeline-tooltip.png"));
      await tab.locator(".timeline-markers button").first().click();
      await tab.getByRole("region", { name: "Selected timeline event" }).waitFor();
      await tab.screenshot({ path: resolve(output, "timeline-event.png"), fullPage: true });
      console.log(resolve(output, "timeline-event.png"));
    }
  }
} finally {
  await browser.close();
}
