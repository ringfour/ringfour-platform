import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, expect, test, vi } from "vitest";
const token = vi.fn(async () => "token");
vi.mock("@auth0/auth0-react", () => ({
  useAuth0: () => ({ getAccessTokenSilently: token }),
}));
vi.mock("@/components/RobotDetailPage", () => ({
  RobotDetailPage: ({
    robotId,
    onBack,
  }: {
    robotId: string;
    onBack: () => void;
  }) => (
    <main>
      Robot details {robotId}
      <button type="button" onClick={onBack}>
        Back to robots
      </button>
    </main>
  ),
}));
vi.mock("@/components/RobotsPage", () => ({
  RobotsPage: () => <main>Manage robot credentials</main>,
}));
import { RobotsWorkspacePage } from "./RobotsWorkspacePage";
function Page() {
  const [id, setId] = useState<string | undefined>();
  return (
    <RobotsWorkspacePage
      organizationId="org"
      robotId={id}
      workspaceRole="owner"
      onSelectRobot={setId}
    />
  );
}
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

test("lists all workspace robots, filters by connectivity and opens robot App detail", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), "http://localhost");
      if (url.pathname.endsWith("/me"))
        return Response.json({ user: { id: "u" } });
      if (url.pathname.endsWith("/app-status"))
        return Response.json({
          connectivity: url.pathname.includes("one") ? "online" : "offline",
          support: "supported",
          last_seen_at: "2026-11-20T10:00:00Z",
          received_at: "2026-11-20T10:00:01Z",
          inventory: null,
        });
      const items = /\/organizations\/[^/]+\/robots$/.test(url.pathname)
        ? [
            { id: "one", organization_id: "org", name: "Robot One" },
            { id: "two", organization_id: "org", name: "Robot Two" },
          ]
        : [];
      return Response.json({ items, next_cursor: null });
    }),
  );
  render(<Page />);
  expect((await screen.findAllByText("Robot One")).length).toBeGreaterThan(0);
  expect((await screen.findAllByText("Robot Two")).length).toBeGreaterThan(0);
  await userEvent.selectOptions(
    screen.getByLabelText("Connectivity"),
    "Online",
  );
  await userEvent.click(
    (await screen.findAllByRole("link", { name: "Robot One" }))[0],
  );
  expect(await screen.findByText("Robot details one")).toBeTruthy();
  await userEvent.click(screen.getByRole("button", { name: "Back to robots" }));
  await userEvent.selectOptions(screen.getByLabelText("Connectivity"), "");
  expect((await screen.findAllByText("Robot Two")).length).toBeGreaterThan(0);
});

test("opens the existing credential enrollment flow from the new inventory view", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), "http://localhost");
      if (url.pathname.endsWith("/me"))
        return Response.json({ user: { id: "u" } });
      return Response.json({ items: [], next_cursor: null });
    }),
  );
  render(<Page />);
  await userEvent.click(
    await screen.findByRole("button", { name: "Manage robots" }),
  );
  expect(screen.getByText("Manage robot credentials")).toBeTruthy();
});
