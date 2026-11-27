import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";

const getAccessTokenSilently = vi.fn(async () => "fixture-token");
vi.mock("@auth0/auth0-react", () => ({
  useAuth0: () => ({ getAccessTokenSilently }),
}));

import { RobotAccessPage } from "./RobotAccessPage";

function setup(role = "owner", fail = false) {
  const changes: string[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), "http://localhost");
      if (init?.method === "POST" || init?.method === "DELETE") {
        changes.push(`${init.method} ${url.pathname}`);
        return fail
          ? Response.json(
              { error: { message: "Stop and uninstall all Apps first" } },
              { status: 409 },
            )
          : new Response(null, { status: 204 });
      }
      if (url.pathname.endsWith("/credentials"))
        return Response.json({ items: [] });
      if (url.pathname.endsWith("/robots/robot"))
        return Response.json({
          id: "robot",
          organization_id: "org",
          name: "Virtual robot",
          enrolled_at: "2026-11-27T00:00:00Z",
        });
      return Response.json({}, { status: 404 });
    }),
  );
  const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
  render(
    <RobotAccessPage
      organizationId="org"
      robotId="robot"
      workspaceRole={role}
    />,
  );
  return { changes, confirm };
}
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});
test("owner unenrolls a robot and gets re-pairing instructions", async () => {
  const { changes } = setup();
  await userEvent.click(
    await screen.findByRole("button", { name: "Unenroll robot" }),
  );
  await waitFor(() =>
    expect(changes).toEqual(["POST /api/v1/robots/robot/unenroll"]),
  );
  expect((await screen.findByRole("status")).textContent).toContain(
    "Restart its daemon",
  );
});
test("cancelling deletion does not mutate a robot", async () => {
  const { changes, confirm } = setup();
  confirm.mockReturnValue(false);
  await userEvent.click(
    await screen.findByRole("button", { name: "Delete robot" }),
  );
  expect(changes).toEqual([]);
});
test("App cleanup refusal is shown and leaves robot controls available", async () => {
  const { changes } = setup("owner", true);
  await userEvent.click(
    await screen.findByRole("button", { name: "Unenroll robot" }),
  );
  expect((await screen.findByRole("alert")).textContent).toContain(
    "Stop and uninstall",
  );
  expect(changes).toHaveLength(1);
});
test.each(["developer", "operator"])(
  "%s cannot see robot deletion or unenrollment",
  async (role) => {
    setup(role);
    await screen.findByText(/Only.*owner/i);
    expect(screen.queryByRole("button", { name: "Delete robot" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Unenroll robot" })).toBeNull();
  },
);
