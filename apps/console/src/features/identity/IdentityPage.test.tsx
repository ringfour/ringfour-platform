import {
  cleanup,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
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

function setup({
  role = "operator",
  soleOwner = false,
  lastOrganization = false,
  denied = false,
  onlyMember = false,
} = {}) {
  let left = false;
  const deletions: string[] = [];
  const org = { id: "alpha", name: "Alpha lab", role };
  const remaining = lastOrganization
    ? []
    : [{ id: "beta", name: "Beta lab", role: "owner" }];
  const fetchSpy = vi.fn(
    async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), "http://localhost");
      if (init?.method === "DELETE") {
        deletions.push(url.pathname);
        if (denied)
          return Response.json(
            {
              error: {
                code: "last_owner",
                message:
                  "Make another member an owner before leaving this organization.",
              },
            },
            { status: 409 },
          );
        left = true;
        return new Response(null, { status: 204 });
      }
      if (url.pathname === "/api/v1/me")
        return Response.json({
          user: { id: "self" },
          memberships: left
            ? []
            : [{ organization_id: "alpha", user_id: "self", role }],
        });
      if (url.pathname === "/api/v1/organizations")
        return Response.json({ items: left ? remaining : [org, ...remaining] });
      if (url.pathname.endsWith("/members"))
        return Response.json({
          items: url.pathname.includes("/alpha/")
            ? [
                { user_id: "self", role, name: "Signed-in member" },
                ...(!onlyMember
                  ? [
                      {
                        user_id: "other",
                        role: soleOwner ? "developer" : "owner",
                        name: "Another member",
                      },
                    ]
                  : []),
              ]
            : [{ user_id: "self", role: "owner", name: "Signed-in member" }],
        });
      if (url.pathname.endsWith("/invitations"))
        return Response.json({ items: [] });
      return Response.json({}, { status: 404 });
    },
  );
  vi.stubGlobal("fetch", fetchSpy);
  const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
  window.history.replaceState(null, "", "/settings/workspace?workspace=alpha");
  render(<App />);
  return { deletions, fetchSpy, confirm };
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  window.history.replaceState(null, "", "/");
});

test.each(["operator", "developer", "owner"])(
  "%s leaves using their own membership route and updates workspace navigation",
  async (role) => {
    const { deletions, confirm } = setup({ role });
    const leave = await screen.findByRole("button", {
      name: "Leave organization",
    });
    await waitFor(() => expect(leave.hasAttribute("disabled")).toBe(false));
    await userEvent.click(leave);
    expect(confirm).toHaveBeenCalledWith(
      expect.stringContaining("Leave Alpha lab?"),
    );
    expect((await screen.findByRole("status")).textContent).toContain(
      "You left Alpha lab.",
    );
    expect(deletions).toEqual(["/api/v1/organizations/alpha/members/me"]);
    expect(screen.queryByRole("button", { name: /^Alpha lab/ })).toBeNull();
    expect(new URL(window.location.href).searchParams.get("workspace")).toBe(
      "beta",
    );
    const selector = screen.getByRole("combobox", { name: "Organization" });
    expect(
      within(selector).queryByRole("option", { name: "Alpha lab" }),
    ).toBeNull();
  },
);

test("cancelled confirmation sends no membership mutation", async () => {
  const { deletions, confirm } = setup();
  confirm.mockReturnValue(false);
  const leave = await screen.findByRole("button", {
    name: "Leave organization",
  });
  await waitFor(() => expect(leave.hasAttribute("disabled")).toBe(false));
  await userEvent.click(leave);
  expect(deletions).toEqual([]);
  expect(screen.getByRole("button", { name: /^Alpha lab/ })).toBeTruthy();
});

test("sole owner with other members must promote another owner to leave", async () => {
  const { deletions } = setup({ role: "owner", soleOwner: true });
  await screen.findByText(
    "Make another member an owner before leaving this organization.",
  );
  expect(
    screen
      .getByRole("button", { name: "Leave organization" })
      .hasAttribute("disabled"),
  ).toBe(true);
  expect(deletions).toEqual([]);
});

test("an API refusal preserves membership and selection", async () => {
  const { deletions } = setup({ role: "owner", denied: true });
  const leave = await screen.findByRole("button", {
    name: "Leave organization",
  });
  await waitFor(() => expect(leave.hasAttribute("disabled")).toBe(false));
  await userEvent.click(leave);
  expect((await screen.findByRole("alert")).textContent).toContain(
    "Make another member an owner",
  );
  expect(deletions).toHaveLength(1);
  expect(screen.getByRole("button", { name: /^Alpha lab/ })).toBeTruthy();
  expect(new URL(window.location.href).searchParams.get("workspace")).toBe(
    "alpha",
  );
});

test("leaving the final organization clears workspace state and shows onboarding", async () => {
  setup({ lastOrganization: true });
  const leave = await screen.findByRole("button", {
    name: "Leave organization",
  });
  await waitFor(() => expect(leave.hasAttribute("disabled")).toBe(false));
  await userEvent.click(leave);
  await screen.findByText("You do not belong to an organization yet.");
  expect(new URL(window.location.href).searchParams.has("workspace")).toBe(
    false,
  );
  expect(
    screen.queryByRole("button", { name: "Leave organization" }),
  ).toBeNull();
});

test.each(["operator", "developer"])(
  "%s has no organization deletion action",
  async (role) => {
    setup({ role });
    await screen.findByRole("button", { name: "Leave organization" });
    expect(
      screen.queryByRole("button", { name: "Delete organization" }),
    ).toBeNull();
  },
);

test("the only member confirms deletion when leaving rather than calling the leave route", async () => {
  const { deletions, confirm } = setup({
    role: "owner",
    soleOwner: true,
    onlyMember: true,
    lastOrganization: true,
  });
  const leave = await screen.findByRole("button", {
    name: "Leave organization",
  });
  await waitFor(() => expect(leave.hasAttribute("disabled")).toBe(false));
  await userEvent.click(leave);
  expect(confirm).toHaveBeenCalledWith(
    expect.stringContaining("Leaving will delete this organization"),
  );
  await screen.findByText("You do not belong to an organization yet.");
  expect(deletions).toEqual(["/api/v1/organizations/alpha"]);
  expect((await screen.findByRole("status")).textContent).toContain(
    "Deleted Alpha lab.",
  );
});

test("sole member can cancel deletion and retain their membership", async () => {
  const { deletions, confirm } = setup({
    role: "owner",
    soleOwner: true,
    onlyMember: true,
  });
  confirm.mockReturnValue(false);
  const leave = await screen.findByRole("button", {
    name: "Leave organization",
  });
  await waitFor(() => expect(leave.hasAttribute("disabled")).toBe(false));
  await userEvent.click(leave);
  expect(confirm).toHaveBeenCalledWith(expect.stringContaining("last member"));
  expect(deletions).toEqual([]);
});

test("owner can explicitly delete an organization with other members after confirmation", async () => {
  const { deletions, confirm } = setup({ role: "owner" });
  const button = await screen.findByRole("button", {
    name: "Delete organization",
  });
  await waitFor(() => expect(button.hasAttribute("disabled")).toBe(false));
  await userEvent.click(button);
  expect(confirm).toHaveBeenCalledWith(
    expect.stringContaining("Delete Alpha lab for all members?"),
  );
  await waitFor(() =>
    expect(new URL(window.location.href).searchParams.get("workspace")).toBe(
      "beta",
    ),
  );
  expect(deletions).toEqual(["/api/v1/organizations/alpha"]);
});
