import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";

const token = vi.fn(async () => "token");
vi.mock("@auth0/auth0-react", () => ({
  useAuth0: () => ({ getAccessTokenSilently: token }),
}));
import { AppStorePage } from "./AppStorePage";
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

test("shows published capability requirements and checks the selected robot before import", async () => {
  const posts: { path: string; body: unknown }[] = [];
  const manifest = {
    app_key: "robot-check",
    release: "1.0.0",
    display: { name: "Robot Check", description: "Read an IMU" },
    capabilities: {
      required: [{ capability: "imu" }],
      optional: [{ capability: "cameras" }],
    },
    targets: [
      {
        os: "linux",
        architecture: "amd64",
        image: "registry/apps/check@sha256:abc",
      },
    ],
    configuration: {
      defaults: { sample_count: 2 },
      schema: {
        properties: {
          sample_count: { type: "integer", minimum: 1, maximum: 20 },
        },
        required: ["sample_count"],
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
          { id: "import", revision_id: "revision" },
          { status: 201 },
        );
      }
      if (url.pathname === "/api/v1/store/apps")
        return Response.json({
          items: [
            {
              id: "app",
              publisher_organization_id: "publisher",
              app_key: "robot-check",
              name: "Robot Check",
              description: "Read an IMU",
            },
          ],
          next_cursor: null,
        });
      if (url.pathname.endsWith("/robots"))
        return Response.json({
          items: [
            { id: "one", organization_id: "org", name: "Robot One" },
            { id: "two", organization_id: "org", name: "Robot Two" },
          ],
          next_cursor: null,
        });
      if (url.pathname.endsWith("/app-imports"))
        return Response.json({ items: [], next_cursor: null });
      if (url.pathname.endsWith("/revisions"))
        return Response.json({
          items: [
            {
              id: "revision",
              app_id: "app",
              publisher_organization_id: "publisher",
              manifest,
              manifest_digest: "sha256:abc",
              visibility: "listed",
              published_at: "2026-11-20T00:00:00Z",
            },
          ],
          next_cursor: null,
        });
      if (url.pathname.endsWith("/app-compatibility"))
        return Response.json({
          status: "compatible",
          checked_at: "2026-11-20T00:00:00Z",
          inventory_observed_at: "2026-11-20T00:00:00Z",
          missing_required: [],
          missing_optional: [],
          missing_native: [],
        });
      return Response.json(
        { error: { message: "unexpected" } },
        { status: 404 },
      );
    }),
  );
  render(<AppStorePage organizationId="org" workspaceRole="developer" />);
  await userEvent.click(
    await screen.findByRole("button", { name: /Robot Check/ }),
  );
  expect(await screen.findByText("imu")).toBeTruthy();
  expect(screen.getByText("cameras")).toBeTruthy();
  await userEvent.selectOptions(
    screen.getByLabelText("Check against robot"),
    "two",
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Check compatibility" }),
  );
  expect(await screen.findByText("compatible")).toBeTruthy();
  await userEvent.click(screen.getByRole("button", { name: "Import release" }));
  expect(
    await screen.findByText("Release imported into this workspace."),
  ).toBeTruthy();
  expect(posts[0]?.path).toBe("/api/v1/organizations/org/app-imports");
  expect(posts[0]?.body).toEqual({ revision_id: "revision", share_id: null });
});
