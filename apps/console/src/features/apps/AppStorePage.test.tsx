import {
  act,
  cleanup,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useEffect } from "react";
import { afterEach, expect, test, vi } from "vitest";
import { followStudioLink, visit } from "@/lib/location";

const token = vi.fn(async () => "token");
vi.mock("@auth0/auth0-react", () => ({
  useAuth0: () => ({ getAccessTokenSilently: token }),
}));

import { AppsPage } from "@/features/apps/AppsPage";
import { AppStorePage } from "@/features/apps/AppStorePage";
import { Publisher } from "@/features/apps/views/Publisher";

afterEach(() => {
  cleanup();
  window.history.replaceState(null, "", "/");
  vi.unstubAllGlobals();
});
function setup({
  installed = false,
  missing = false,
  owned = false,
  imported = false,
} = {}) {
  const posts: { path: string; body: unknown }[] = [];
  const app = {
    id: "app",
    publisher_organization_id: owned ? "org" : "publisher",
    publisher_type: owned ? "workspace" : "ringfour",
    publisher_name: owned ? "Workshop" : "Ringfour",
    publisher_user_name: owned ? "Ada Publisher" : undefined,
    app_key: "camera",
    name: "Camera Snapshot Loop",
    description: "Save a photo every 30 seconds",
  };
  const revision = {
    id: "revision",
    app_id: "app",
    visibility: owned ? "unlisted" : "listed",
    publisher_organization_id: app.publisher_organization_id,
    published_at: "2026-11-25T10:00:00Z",
    manifest: {
      release: "1.0.0",
      display: {
        description: "First release stores a photo every thirty seconds.",
      },
      capabilities: { required: [{ capability: "cameras" }], optional: [] },
      configuration: {
        defaults: { interval: 30 },
        schema: { properties: { interval: { type: "number" } } },
      },
    },
  };
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), "http://localhost");
      if (init?.method === "POST") {
        posts.push({ path: url.pathname, body: JSON.parse(String(init.body)) });
        return Response.json(
          { installation_id: "installation", operation_id: "operation" },
          { status: 202 },
        );
      }
      if (url.pathname === "/api/v1/store/apps")
        return Response.json({ items: [app] });
      if (url.pathname === "/api/v1/organizations/org/apps")
        return Response.json({ items: owned ? [app] : [] });
      if (url.pathname.endsWith("/app-runs"))
        return Response.json({ items: [] });
      if (url.pathname === "/api/v1/apps/app") return Response.json(app);
      if (url.pathname.endsWith("/revisions"))
        return Response.json({
          items: [
            revision,
            {
              ...revision,
              id: "second-revision",
              published_at: "2026-11-26T10:00:00Z",
              manifest: {
                ...revision.manifest,
                release: "2.0.0",
                display: {
                  description:
                    "Second release saves photos at a configurable interval.",
                },
              },
            },
          ],
        });
      if (url.pathname.endsWith("/robots"))
        return Response.json({
          items: [
            { id: "one", name: "Robot One" },
            { id: "two", name: "Robot Two" },
          ],
        });
      if (url.pathname.endsWith("/app-installations"))
        return Response.json({
          items:
            installed && url.pathname.includes("/two/")
              ? [
                  {
                    id: "installation-b",
                    app_id: "app",
                    robot_id: "two",
                    installed: { revision_id: "revision" },
                    candidate: null,
                  },
                ]
              : [],
        });
      if (url.pathname.endsWith("/app-compatibility"))
        return Response.json({
          status: missing ? "missing_required" : "compatible",
          missing_required: missing ? [{ capability: "cameras" }] : [],
          missing_optional: [],
          missing_native: [],
        });
      if (url.pathname.endsWith("/app-operations/operation"))
        return Response.json({
          id: "operation",
          installation_id: "binding-b",
          organization_id: "org",
          state: "queued",
          phase: "queued",
        });
      if (url.pathname.endsWith("/app-imports"))
        return Response.json({
          items: imported ? [{ app_id: "app", revision_id: "revision" }] : [],
        });
      return Response.json(
        { error: { message: "unexpected" } },
        { status: 404 },
      );
    }),
  );
  return posts;
}
function Page() {
  useEffect(() => {
    document.addEventListener("click", followStudioLink);
    return () => document.removeEventListener("click", followStudioLink);
  }, []);
  return <AppStorePage organizationId="org" workspaceRole="developer" />;
}

test("Hub cards open dedicated listings and install on the explicitly selected robot", async () => {
  const posts = setup();
  window.history.replaceState(null, "", "/hub?workspace=org");
  render(<Page />);
  await userEvent.click(
    await screen.findByRole("link", { name: /Camera Snapshot Loop/ }),
  );
  expect(window.location.pathname).toBe("/hub/app");
  expect(await screen.findByText("Required")).toBeTruthy();
  await userEvent.click(screen.getByRole("button", { name: "Choose robot" }));
  const dialog = within(screen.getByRole("dialog"));
  await userEvent.click(dialog.getByRole("radio", { name: /Robot Two/ }));
  const button = await dialog.findByRole("button", {
    name: "Install on Robot Two",
  });
  await waitFor(() => expect(button).toHaveProperty("disabled", false));
  await userEvent.click(button);
  await waitFor(() => expect(posts).toHaveLength(1));
  expect(posts[0]).toEqual({
    path: "/api/v1/robots/two/app-installations",
    body: {
      revision_id: "second-revision",
      share_id: null,
      configuration: { interval: 30 },
    },
  });
  expect(window.location.search).toContain("operation=operation");
  expect(window.location.search).toContain("robot=two");
  expect(posts.some((post) => post.path.endsWith("/start"))).toBe(false);
});

test("the description follows the selected release", async () => {
  setup();
  window.history.replaceState(null, "", "/hub/app?workspace=org");
  render(<Page />);
  expect(
    await screen.findByText(
      "Second release saves photos at a configurable interval.",
    ),
  ).toBeTruthy();
  await userEvent.click(screen.getByText("Release details"));
  await userEvent.selectOptions(
    screen.getByRole("combobox", { name: "Release" }),
    "revision",
  );
  expect(
    await screen.findByText(
      "First release stores a photo every thirty seconds.",
    ),
  ).toBeTruthy();
  expect(screen.queryByText("Save a photo every 30 seconds")).toBeNull();
});

test("same release installed offers exact Open App and never another install", async () => {
  setup({ installed: true });
  window.history.replaceState(
    null,
    "",
    "/hub/app?workspace=org&robot=two&release=revision",
  );
  render(<Page />);
  expect(
    (
      await screen.findByRole("link", { name: "Open on Robot Two" })
    ).getAttribute("href"),
  ).toBe("/apps/app/overview?workspace=org&installation=installation-b");
  expect(screen.queryByRole("button", { name: /Review install/ })).toBeNull();
});

test("missing capabilities explain why the selected robot cannot install", async () => {
  setup({ missing: true });
  window.history.replaceState(
    null,
    "",
    "/hub/app?workspace=org&robot=two&install=1",
  );
  render(<Page />);
  expect(
    await screen.findByText("Required capability missing: Cameras"),
  ).toBeTruthy();
  expect(
    within(screen.getByRole("dialog")).getByRole("button", {
      name: "Install on Robot Two",
    }),
  ).toHaveProperty("disabled", true);
});

test("refresh reconstructs listing, robot and pending operation from the link", async () => {
  setup();
  window.history.replaceState(
    null,
    "",
    "/hub/app?workspace=org&robot=two&release=revision&operation=operation",
  );
  render(<Page />);
  expect(await screen.findByText("Works with Robot Two")).toBeTruthy();
  expect(await screen.findByText(/Accepted|Queued/)).toBeTruthy();
  expect(window.location.search).toContain("operation=operation");
});

test("a workspace named Ringfour never receives the official publisher label", () => {
  render(
    <Publisher
      app={{
        id: "private",
        name: "Camera",
        app_key: "camera",
        description: "Photos",
        publisher_organization_id: "workspace",
        publisher_name: "Ringfour",
        publisher_type: "workspace",
        created_at: "2026-11-25T10:00:00Z",
      }}
    />,
  );
  expect(screen.getByText("Ringfour · Workspace App")).toBeTruthy();
  expect(screen.queryByText("Ringfour · Official")).toBeNull();
});

test("Hub update review carries the selected release and installation", async () => {
  setup({ installed: true });
  window.history.replaceState(
    null,
    "",
    "/hub/app?workspace=org&robot=two&release=second-revision",
  );
  render(<Page />);
  const link = await screen.findByRole("link", { name: "Update on Robot Two" });
  const url = new URL(link.getAttribute("href")!, window.location.origin);
  expect(url.searchParams.get("release")).toBe("second-revision");
  expect(url.searchParams.get("installation")).toBe("installation-b");
});

test("operators can browse an eligible listing and see who can install", async () => {
  setup();
  window.history.replaceState(null, "", "/hub/app?workspace=org&robot=two");
  render(<AppStorePage organizationId="org" workspaceRole="operator" />);
  expect(
    await screen.findByText(
      "A workspace owner or developer can install this App.",
    ),
  ).toBeTruthy();
  expect(
    screen.queryByRole("button", { name: "Install on Robot Two" }),
  ).toBeNull();
});

test("an unlisted workspace publication is in Apps with its publisher before any install", async () => {
  setup({ owned: true });
  window.history.replaceState(null, "", "/apps?workspace=org");
  const view = render(
    <AppsPage organizationId="org" workspaceRole="developer" />,
  );
  const card = await screen.findByRole("link", {
    name: /Camera Snapshot Loop/,
  });
  expect(card.getAttribute("href")).toBe("/apps/app/overview?workspace=org");
  expect(within(card).getByText("Ada Publisher · Workshop")).toBeTruthy();

  act(() => {
    visit("/apps/app/overview?workspace=org&install=1&release=revision");
  });
  view.rerender(
    <AppsPage
      organizationId="org"
      workspaceRole="developer"
      focusAppId="app"
    />,
  );
  await screen.findByRole("heading", { name: "Install Camera Snapshot Loop" });
  expect(window.location.pathname).toBe("/apps/app/overview");
});

test.each([{ owned: true }, { imported: true }])(
  "Hub excludes Apps already in the drawer (%o)",
  async (options) => {
    setup(options);
    window.history.replaceState(null, "", "/hub?workspace=org");
    render(<Page />);
    await screen.findByText("No Apps are available to this workspace yet.");
    expect(
      screen.queryByRole("link", { name: /Camera Snapshot Loop/ }),
    ).toBeNull();
  },
);
