import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";

const token = vi.fn(async () => "token");
vi.mock("@auth0/auth0-react", () => ({
  useAuth0: () => ({ getAccessTokenSilently: token }),
}));
import { RobotDetailPage } from "./RobotDetailPage";
const json = (data: unknown, status = 200) => Response.json(data, { status });
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

test("shows App inventory and starts the installed revision with an explicit binding", async () => {
  const calls: { url: string; init?: RequestInit }[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      calls.push({ url, init });
      if (url === "/api/v1/robots/robot")
        return json({ id: "robot", name: "base101", organization_id: "org" });
      if (url === "/api/v1/robots/robot/app-status")
        return json({
          connectivity: "online",
          support: "supported",
          last_seen_at: "2026-11-20T08:00:00Z",
          inventory: null,
        });
      if (url.endsWith("/app-installations"))
        return json({
          items: [
            {
              id: "installation",
              app_id: "app",
              robot_id: "robot",
              installed: {
                revision_id: "revision",
                configuration_revision_id: "configuration",
                image: "registry/app@sha256:abc",
                installed_at: "today",
                observed_at: "today",
              },
              candidate: null,
              last_observed_at: "today",
            },
          ],
          next_cursor: null,
        });
      if (url.includes("/app-runs?"))
        return json({ items: [], next_cursor: null });
      if (url.includes("/app-logbook?"))
        return json({ items: [], next_cursor: null });
      if (url === "/api/v1/apps/app")
        return json({
          id: "app",
          name: "Robot Check",
          description: "Read an IMU",
        });
      if (url === "/api/v1/app-installations/installation/start")
        return json(
          {
            operation_id: "operation",
            run_id: "run",
            installation_id: "installation",
          },
          202,
        );
      if (url === "/api/v1/app-operations/operation")
        return json({ id: "operation", state: "queued", phase: "queued" });
      return json({ error: { message: "unexpected" } }, 404);
    }),
  );
  render(
    <RobotDetailPage
      robotId="robot"
      workspaceRole="owner"
      onBack={() => {}}
      onManage={() => {}}
    />,
  );
  expect(await screen.findByText("Online")).toBeTruthy();
  expect(await screen.findByText("Robot Check")).toBeTruthy();
  await userEvent.click(screen.getByRole("button", { name: "Start" }));
  expect(await screen.findByText(/Accepted/)).toBeTruthy();
  const start = calls.find((call) => call.url.endsWith("/start"));
  expect(JSON.parse(String(start?.init?.body))).toEqual({
    expected_revision_id: "revision",
    expected_configuration_revision_id: "configuration",
  });
  expect(
    (start?.init?.headers as Record<string, string>)["Idempotency-Key"],
  ).toBeTruthy();
});
