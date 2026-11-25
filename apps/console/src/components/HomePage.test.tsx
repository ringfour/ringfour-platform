import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, expect, test, vi } from "vitest";
const token = async () => "token";
vi.mock("@auth0/auth0-react", () => ({
  useAuth0: () => ({ getAccessTokenSilently: token }),
}));
import { HomePage } from "./HomePage";
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function setup({
  robots = [{ id: "r1", organization_id: "org", name: "Workshop rover" }],
  runs = [],
  interactions = [],
  unavailableStatus = false,
}: {
  robots?: unknown[];
  runs?: unknown[];
  interactions?: unknown[];
  unavailableStatus?: boolean;
} = {}) {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), "http://localhost");
      if (url.pathname.endsWith("/me"))
        return Response.json({ user: { id: "u" } });
      if (url.pathname.endsWith("/app-status") && unavailableStatus)
        return new Response(null, { status: 503 });
      if (url.pathname.endsWith("/app-status"))
        return Response.json({
          connectivity: "offline",
          last_seen_at: "2026-11-20T10:00:00Z",
          support: "supported",
          received_at: "2026-11-20T10:00:01Z",
          inventory: {
            observed_at: "2026-11-20T10:00:00Z",
            source: {
              kind: "agent_welcome",
              profile: { name: "Base101", model: "virtual", version: "1" },
            },
            capabilities: ["imu"],
            instances: {},
          },
        });
      if (url.pathname === "/api/v1/apps/app")
        return Response.json({
          id: "app",
          name: "Inspection",
          description: "Inspect",
        });
      if (url.pathname === "/api/v1/apps/app/revisions")
        return Response.json({
          items: [
            {
              id: "rev",
              app_id: "app",
              manifest: { release: "1.2.0" },
              published_at: "2026-11-20T00:00:00Z",
            },
          ],
          next_cursor: null,
        });
      if (url.pathname.endsWith("/artifacts"))
        return Response.json({ items: [] });
      let items: unknown[] = [];
      if (/\/organizations\/[^/]+\/robots$/.test(url.pathname)) items = robots;
      else if (url.pathname.endsWith("/app-runs")) items = runs;
      else if (url.pathname.endsWith("/interactions")) items = interactions;
      return Response.json({ items, next_cursor: null });
    }),
  );
  render(<HomePage organizationId="org" workspaceRole="viewer" />);
}
const run = (id: string, robot = "r1", state: string = "failed") => ({
  id,
  organization_id: "org",
  robot_id: robot,
  installation_id: "i",
  start_operation_id: "op",
  snapshot: {
    app_id: "app",
    revision_id: "rev",
    publisher_organization_id: "publisher",
    configuration_revision_id: "cfg",
    configuration: {},
    image: "registry/app@sha256:abc",
  },
  state,
  requested_by: "user",
  requested_at: "2026-11-20T10:00:00Z",
  completed_at: "2026-11-20T10:01:00Z",
  failure:
    state === "failed" ? { code: "failed", message: "Sample failed" } : null,
});

test("shows every robot with online/offline current work and inventory timestamps", async () => {
  setup({
    robots: [
      { id: "r1", organization_id: "org", name: "Workshop rover" },
      { id: "r2", organization_id: "org", name: "Offline cart" },
    ],
  });
  expect((await screen.findAllByText("Workshop rover")).length).toBeGreaterThan(
    0,
  );
  expect(screen.getAllByText("Offline cart").length).toBeGreaterThan(0);
  expect(screen.getAllByText("Offline").length).toBeGreaterThan(0);
  expect(screen.getAllByText(/inventory received/).length).toBeGreaterThan(0);
  expect(
    screen.getAllByText(/No active run last reported/).length,
  ).toBeGreaterThan(0);
});

test("surfaces failed runs and pending HITL while viewer controls remain read-only", async () => {
  setup({
    runs: [run("run-failed")],
    interactions: [
      {
        created_at: "2026-11-20T10:00:00Z",
        request_id: "request",
        robot_id: "r1",
        execution_id: "run-failed",
        prompt: "Continue?",
        choices: [{ value: "yes", label: "Yes" }],
        deadline: null,
        state: "pending",
        response: null,
      },
    ],
  });
  expect(await screen.findByText("Continue?")).toBeTruthy();
  expect(screen.getByText("Sample failed")).toBeTruthy();
  expect(screen.getByRole("button", { name: "Yes" })).toHaveProperty(
    "disabled",
    true,
  );
  expect(
    screen
      .getByRole("link", { name: "Inspect App run ↗" })
      .getAttribute("href"),
  ).toContain("run-failed");
});

test("shows the no-robot empty state", async () => {
  setup({ robots: [] });
  expect(
    await screen.findByText("Connect a robot and choose an App"),
  ).toBeTruthy();
});

test("keeps unavailable connectivity unknown instead of assuming the robot is online", async () => {
  setup({ unavailableStatus: true });
  expect(await screen.findByText("Connectivity unknown")).toBeTruthy();
  expect(screen.queryByText("Online")).toBeNull();
});
