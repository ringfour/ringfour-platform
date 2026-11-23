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
      Details for {robotId}
      <button type="button" onClick={onBack}>
        Back to robots
      </button>
    </main>
  ),
}));
vi.mock("@/components/RobotsPage", () => ({
  RobotsPage: () => <main>Manage robots</main>,
}));

import { FleetPage } from "./FleetPage";

function Page() {
  const [id, setId] = useState<string | undefined>();
  return (
    <FleetPage
      robotId={id}
      onSelectRobot={setId}
      preferredOrganizationId="org"
      onOrganizationChange={() => {}}
    />
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});
test("replaces the list with a robot detail and returns to the register", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn(
      async (input: RequestInfo | URL) =>
        new Response(
          JSON.stringify(
            String(input).endsWith("/organizations")
              ? { items: [{ id: "org", name: "Acme" }], next_cursor: null }
              : {
                  items: [
                    { id: "one", name: "Robot One", enrolled_at: "today" },
                    { id: "two", name: "Robot Two", enrolled_at: null },
                  ],
                  next_cursor: null,
                },
          ),
        ),
    ),
  );
  render(<Page />);
  await userEvent.click(await screen.findByRole("button", { name: /View Robot One/ }));
  expect(screen.getByText("Details for one")).toBeTruthy();
  expect(
    screen.queryByRole("complementary", { name: "Robot list" }),
  ).toBeNull();
  await userEvent.click(screen.getByRole("button", { name: "Back to robots" }));
  expect(await screen.findByRole("button", { name: "View Robot Two" })).toBeTruthy();
  await userEvent.click(screen.getByRole("button", { name: "Add robot" }));
  expect(screen.getByText("Manage robots")).toBeTruthy();
});
