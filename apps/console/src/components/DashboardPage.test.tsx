import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";

const { getToken } = vi.hoisted(() => ({
  getToken: vi.fn(async () => "token"),
}));
vi.mock("@auth0/auth0-react", () => ({
  useAuth0: () => ({ getAccessTokenSilently: getToken }),
}));

import { DashboardPage } from "./DashboardPage";

function response(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

test("loads paginated fleet and missions, statuses, and filters robots", async () => {
  const navigate = vi.fn();
  const fetcher = vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input);
    if (url === "/api/v1/organizations")
      return response({
        items: [{ id: "org", name: "Acme Robotics", role: "owner" }],
        next_cursor: null,
      });
    if (url === "/api/v1/organizations/org/robots")
      return response({
        items: [
          {
            id: "r1",
            name: "base101",
            enrolled_at: "today",
            created_at: "today",
          },
        ],
        next_cursor: "next",
      });
    if (url === "/api/v1/organizations/org/robots?cursor=next")
      return response({
        items: [
          { id: "r2", name: "other", enrolled_at: null, created_at: "today" },
        ],
        next_cursor: null,
      });
    if (url === "/api/v1/organizations/org/missions")
      return response({
        items: [
          {
            id: "m1",
            name: "diagnostics",
            latest_version: 2,
            created_at: "2026-01-01",
          },
        ],
        next_cursor: null,
      });
    if (url === "/api/v1/robots/r1/status")
      return response({
        online: true,
        daemon_version: "0.1.0",
        last_seen_at: null,
      });
    if (url === "/api/v1/robots/r2/status")
      return response({ error: { message: "offline" } }, 503);
    return response({}, 404);
  });
  vi.stubGlobal("fetch", fetcher);
  render(<DashboardPage onNavigate={navigate} />);
  expect(await screen.findByText("base101")).toBeTruthy();
  expect(await screen.findByText("diagnostics")).toBeTruthy();
  expect(await screen.findByText("unknown")).toBeTruthy();
  expect(screen.getByText("1 status unavailable")).toBeTruthy();
  expect(screen.getByText("v2")).toBeTruthy();
  await userEvent.click(
    screen.getByRole("button", { name: "Open fleet for base101" }),
  );
  expect(navigate).toHaveBeenCalledWith("robots", "r1");
  await userEvent.type(
    screen.getByRole("searchbox", { name: "Search robots" }),
    "other",
  );
  expect(screen.queryByText("base101")).toBeNull();
  expect(screen.getByText("other")).toBeTruthy();
  await userEvent.click(screen.getByRole("button", { name: /manage fleet/i }));
  expect(navigate).toHaveBeenCalledWith("robots");
  expect(
    fetcher.mock.calls.some(([url]) => String(url).includes("cursor=next")),
  ).toBe(true);
});

test("shows useful empty and error states without invented metrics", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === "/api/v1/organizations")
        return response({
          items: [{ id: "org", name: "Lab", role: "operator" }],
          next_cursor: null,
        });
      if (url.endsWith("/robots"))
        return response({ items: [], next_cursor: null });
      return response({ error: { message: "unavailable" } }, 503);
    }),
  );
  render(<DashboardPage onNavigate={vi.fn()} />);
  expect(await screen.findByText("No robots connected yet")).toBeTruthy();
  await waitFor(() =>
    expect(
      screen.getAllByText("Mission catalog is unavailable.").length,
    ).toBeGreaterThan(0),
  );
  expect(screen.queryByText("No missions published")).toBeNull();
});
