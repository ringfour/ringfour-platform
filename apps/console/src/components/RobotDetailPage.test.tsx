import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";

const token = vi.fn(async () => "token");
vi.mock("@auth0/auth0-react", () => ({
  useAuth0: () => ({ getAccessTokenSilently: token }),
}));

import { RobotDetailPage } from "./RobotDetailPage";

const json = (data: unknown, status = 200) =>
  new Response(JSON.stringify(data), {
    status,
    headers: { "Content-Type": "application/json" },
  });
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});
test("shows robot status and run output, and submits an explicit restart", async () => {
  const calls: { url: string; init?: RequestInit }[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      calls.push({ url, init });
      if (url === "/api/v1/robots/robot")
        return json({
          id: "robot",
          name: "base101",
          organization_id: "org",
          created_at: "today",
        });
      if (url === "/api/v1/robots/robot/status")
        return json({
          online: true,
          daemon_version: "0.1.0",
          protocol_version: 1,
          last_seen_at: "today",
        });
      if (url === "/api/v1/robots/robot/executions")
        return json({
          items: [
            {
              id: "run-1",
              state: "succeeded",
              created_at: "today",
              updated_at: "today",
              exit_code: 0,
            },
          ],
          next_cursor: null,
        });
      if (url === "/api/v1/organizations/org/deployments")
        return json({
          items: [
            {
              id: "deploy",
              robot_id: "robot",
              selected_revision_id: "revision",
              candidate_revision_id: null,
              observed_state: "ready",
              desired_state: "stopped",
            },
          ],
          next_cursor: null,
        });
      if (url.includes("/output?"))
        return json({
          data_base64: url.includes("stdout") ? btoa("hello") : "",
          next_offset: url.includes("stdout") ? 5 : 0,
          truncated: false,
        });
      if (url === "/api/v1/deployments/deploy/restart")
        return json(
          { operation_id: "operation", execution_id: "new-run" },
          202,
        );
      return json({ error: { message: "unexpected" } }, 404);
    }),
  );
  render(
    <RobotDetailPage robotId="robot" onBack={() => {}} onManage={() => {}} />,
  );
  expect(await screen.findByText("Online")).toBeTruthy();
  await userEvent.click(await screen.findByRole("button", { name: "run-1" }));
  expect(await screen.findByText("hello")).toBeTruthy();
  await userEvent.click(
    screen.getByRole("button", { name: "Restart mission" }),
  );
  expect(await screen.findByRole("status")).toHaveProperty(
    "textContent",
    expect.stringContaining("new-run"),
  );
  expect(
    calls.find((call) => call.url.endsWith("/restart"))?.init?.headers,
  ).toHaveProperty("Idempotency-Key");
});
