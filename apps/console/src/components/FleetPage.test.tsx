import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, expect, test, vi } from "vitest";

const token = vi.fn(async () => "token");
vi.mock("@auth0/auth0-react", () => ({
  useAuth0: () => ({ getAccessTokenSilently: token }),
}));
vi.mock("@/components/RobotDetailPage", () => ({
  RobotDetailPage: ({ robotId }: { robotId: string }) => (
    <main>Details for {robotId}</main>
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
test("keeps the robot list visible while switching the selected detail", async () => {
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
  await userEvent.click(
    await screen.findByRole("button", { name: /Robot One/ }),
  );
  expect(screen.getByText("Details for one")).toBeTruthy();
  await userEvent.click(screen.getByRole("button", { name: /Robot Two/ }));
  expect(screen.getByText("Details for two")).toBeTruthy();
  expect(
    screen.getByRole("complementary", { name: "Robot list" }),
  ).toBeTruthy();
  await userEvent.click(screen.getByRole("button", { name: "Refresh" }));
  expect(screen.getByText("Details for two")).toBeTruthy();
  await userEvent.click(screen.getByRole("button", { name: "Add robot" }));
  expect(screen.getByText("Manage robots")).toBeTruthy();
});
