import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";
import { MissionControls } from "./MissionControls";

const json = (data: unknown, status = 200) =>
  new Response(JSON.stringify(data), {
    status,
    headers: { "Content-Type": "application/json" },
  });
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

test("prepares the chosen revision and starts only after it is selected", async () => {
  let deployment: Record<string, unknown> | null = null;
  const calls: { url: string; init?: RequestInit }[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      calls.push({ url, init });
      if (url.endsWith("/deployments") && !init?.method)
        return json({
          items: deployment ? [deployment] : [],
          next_cursor: null,
        });
      if (
        url === "/api/v1/robots/robot/deployments" &&
        init?.method === "POST"
      ) {
        deployment = {
          id: "deploy",
          robot_id: "robot",
          selected_revision_id: "revision",
          candidate_revision_id: null,
          observed_state: "ready",
          last_error: null,
        };
        return json(
          { deployment_id: "deploy", operation_id: "prepare-op" },
          202,
        );
      }
      if (url === "/api/v1/deployments/deploy/start")
        return json(
          { operation_id: "start-op", execution_id: "execution" },
          202,
        );
      return json({ error: { message: "unexpected" } }, 404);
    }),
  );
  const getToken = async () => "token";
  render(
    <MissionControls
      organizationId="org"
      revisionId="revision"
      robots={[{ id: "robot", name: "base101" }]}
      getToken={getToken}
    />,
  );
  await userEvent.selectOptions(screen.getByLabelText("Robot"), "robot");
  await userEvent.click(
    await screen.findByRole("button", { name: "Prepare this revision" }),
  );
  expect(
    calls.find((call) => call.url === "/api/v1/robots/robot/deployments")?.init
      ?.body,
  ).toBe(JSON.stringify({ mission_revision_id: "revision" }));
  await userEvent.click(
    await screen.findByRole("button", { name: "Start mission" }),
  );
  expect(await screen.findByText(/execution execution/)).toBeTruthy();
  expect(
    calls.find((call) => call.url.endsWith("/start"))?.init?.headers,
  ).toHaveProperty("Idempotency-Key");
});

test("shows conflict errors without claiming the mission started", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) =>
      init?.method === "POST"
        ? json(
            {
              error: {
                code: "execution_conflict",
                message: "Another execution is active.",
              },
            },
            409,
          )
        : json({
            items: [
              {
                id: "deploy",
                robot_id: "robot",
                selected_revision_id: "revision",
                candidate_revision_id: null,
                observed_state: "ready",
              },
            ],
            next_cursor: null,
          }),
    ),
  );
  render(
    <MissionControls
      organizationId="org"
      revisionId="revision"
      robots={[{ id: "robot", name: "base101" }]}
      getToken={async () => "token"}
    />,
  );
  await userEvent.selectOptions(screen.getByLabelText("Robot"), "robot");
  await userEvent.click(
    await screen.findByRole("button", { name: "Start mission" }),
  );
  expect(await screen.findByRole("alert")).toHaveProperty(
    "textContent",
    "Another execution is active.",
  );
});
