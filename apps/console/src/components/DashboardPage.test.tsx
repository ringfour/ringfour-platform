import { cleanup, render, screen, within } from "@testing-library/react";
import { afterEach, expect, test, vi } from "vitest";
import { DashboardPage } from "./DashboardPage";

const token = async () => "token";
vi.mock("@auth0/auth0-react", () => ({
  useAuth0: () => ({ getAccessTokenSilently: token }),
}));
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});
function setup(failed: boolean, unavailable = false) {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), "http://localhost");
      const end = url.pathname.split("/").at(-1);
      if (unavailable && end === "deployments")
        return new Response(null, { status: 503 });
      const items: Record<string, unknown[]> = {
        robots: [{ id: "r", name: "Workshop rover" }],
        missions: [{ id: "app", name: "Inspection" }],
        revisions: [{ id: "v", version: 5 }],
        members: [],
        interactions: [],
        events: [],
        deployments: failed
          ? [
              {
                id: "d",
                robot_id: "r",
                selected_revision_id: "v",
                desired_state: "running",
                observed_state: "failed",
              },
            ]
          : [],
        executions: [
          {
            id: "old",
            state: "failed",
            updated_at: "2026-11-20T10:00:00Z",
            mission_revision_id: "v",
            actor_user_id: "secret-id",
          },
        ],
      };
      return Response.json(
        end === "me"
          ? { user: { id: "u" } }
          : end === "artifacts"
            ? { artifacts: [] }
            : { items: items[end ?? ""] ?? [], next_cursor: null },
      );
    }),
  );
  render(<DashboardPage preferredOrganizationId="org" onNavigate={() => {}} />);
}
test("prioritises the current deployment mismatch and keeps past failures in history", async () => {
  setup(true);
  expect(await screen.findByText("1 operation needs attention")).toBeTruthy();
  expect(
    screen
      .getByRole("link", { name: /Inspect deployment/ })
      .getAttribute("href"),
  ).toBe("/robots/r?workspace=org&tab=Deployment");
  expect(
    screen.getAllByRole("heading", { level: 2 }).map((h) => h.textContent),
  ).toEqual([
    "Needs attention",
    "Recent outcomes",
    "Robots and current work",
    "Recent timeline",
  ]);
  expect(within(screen.getByRole("list")).getByText("Failed")).toBeTruthy();
  expect(screen.queryByText("secret-id")).toBeNull();
});
test("historical failure alone leaves attention clear", async () => {
  setup(false);
  expect(await screen.findByText("Nothing needs your attention.")).toBeTruthy();
});
test("missing deployment data does not report an all-clear", async () => {
  setup(false, true);
  expect(
    await screen.findByText("Deployment information unavailable."),
  ).toBeTruthy();
  expect(screen.queryByText("Nothing needs your attention.")).toBeNull();
});
