import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";

const getAccessTokenSilently = vi.fn(async () => "fixture-token");
vi.mock("@auth0/auth0-react", () => ({
  useAuth0: () => ({
    isAuthenticated: true,
    isLoading: false,
    getAccessTokenSilently,
    logout: vi.fn(),
    loginWithRedirect: vi.fn(),
  }),
}));

import App from "@/App";

function setup(owner = false, invalid = false) {
  let joined = false;
  const posts: { path: string; body: Record<string, unknown> }[] = [];
  const organization = {
    id: "lab",
    name: "Invitation lab",
    role: owner ? "owner" : "operator",
  };
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = new URL(String(input), "http://localhost").pathname;
      if (init?.method === "POST") {
        const body = JSON.parse(String(init.body || "{}"));
        posts.push({ path, body });
        if (path.endsWith("/invitation-codes"))
          return Response.json(
            {
              code: "fixture-shareable-code",
              invitation: { id: "invitation", email: null },
            },
            { status: 201 },
          );
        if (path.endsWith("/preview"))
          return invalid
            ? Response.json(
                { error: { message: "Invitation expired" } },
                { status: 409 },
              )
            : Response.json({
                organization_id: "lab",
                organization_name: "Invitation lab",
                role: "operator",
                email: null,
              });
        if (path.endsWith("/accept")) {
          joined = true;
          return Response.json({
            organization_id: "lab",
            user_id: "self",
            role: "operator",
            created_at: "2026-11-27T00:00:00Z",
          });
        }
      }
      if (path === "/api/v1/me")
        return Response.json({ user: { id: "self" }, memberships: [] });
      if (path === "/api/v1/organizations")
        return Response.json({ items: owner || joined ? [organization] : [] });
      if (path.endsWith("/members"))
        return Response.json({
          items: [
            { user_id: "self", role: organization.role, name: "Current user" },
          ],
        });
      if (path.endsWith("/invitations")) return Response.json({ items: [] });
      return Response.json({}, { status: 404 });
    }),
  );
  window.history.replaceState(null, "", "/settings/workspace");
  render(<App />);
  return posts;
}
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  window.history.replaceState(null, "", "/");
});
test("owner creates a copyable Operator code without sending email", async () => {
  const posts = setup(true);
  await userEvent.selectOptions(
    await screen.findByRole("combobox", { name: "Invitation code role" }),
    "operator",
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Create invitation code" }),
  );
  const code = (await screen.findByRole("textbox", {
    name: "Share this invitation code",
  })) as HTMLInputElement;
  expect(code.value).toBe("fixture-shareable-code");
  expect(posts).toEqual([
    {
      path: "/api/v1/organizations/lab/invitation-codes",
      body: { role: "operator" },
    },
  ]);
});
test("a signed-in account with no organization can preview a code then join without email", async () => {
  const posts = setup();
  await userEvent.type(
    await screen.findByRole("textbox", { name: "Invitation code" }),
    "fixture-shareable-code",
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Check invitation" }),
  );
  await screen.findByText("Invitation lab", { selector: "strong" });
  await userEvent.click(
    screen.getByRole("button", { name: "Join organization" }),
  );
  await waitFor(() =>
    expect(new URL(window.location.href).searchParams.get("workspace")).toBe(
      "lab",
    ),
  );
  expect((await screen.findByRole("status")).textContent).toContain(
    "Joined Invitation lab as operator.",
  );
  expect(posts.map((p) => p.path)).toEqual([
    "/api/v1/invitations/preview",
    "/api/v1/invitations/accept",
  ]);
  expect(
    screen.queryByRole("button", { name: "Create invitation code" }),
  ).toBeNull();
});
test("expired code reports its error without adding membership", async () => {
  const posts = setup(false, true);
  await userEvent.type(
    await screen.findByRole("textbox", { name: "Invitation code" }),
    "expired-code",
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Check invitation" }),
  );
  expect((await screen.findByRole("alert")).textContent).toContain(
    "Invitation expired",
  );
  expect(posts).toHaveLength(1);
  expect(
    screen.queryByRole("button", { name: "Join organization" }),
  ).toBeNull();
});
