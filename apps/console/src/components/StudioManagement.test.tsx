import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";
import type { App, Installation, Revision } from "@/lib/apps";

const token = vi.fn(async () => "token");
vi.mock("@auth0/auth0-react", () => ({
  useAuth0: () => ({ getAccessTokenSilently: token }),
}));

import { AppReleaseManagement } from "@/features/apps/AppReleaseManagement";
import { RobotAccessPage } from "@/features/robots/RobotAccessPage";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  window.history.replaceState(null, "", "/");
  vi.unstubAllGlobals();
});
const app = { id: "camera", name: "Camera Snapshot Loop" } as App;
const installation = {
  id: "binding-b",
  robot_id: "robot-b",
  app_id: "camera",
  installed: { revision_id: "v1", configuration_revision_id: "c1" },
  candidate: null,
} as Installation;
const revision = (id: string, release: string): Revision => ({
  id,
  app_id: "camera",
  publisher_organization_id: "official",
  manifest_digest: "sha256:" + "a".repeat(64),
  visibility: "listed",
  published_at: `2026-11-2${release === "0.9" ? "3" : release === "1.0" ? "4" : "5"}T10:00:00Z`,
  manifest: {
    app_key: "camera",
    display: { name: "Camera Snapshot Loop", description: "Take photos" },
    targets: [],
    release,
    capabilities: { required: [{ capability: "cameras" }], optional: [] },
    configuration: {
      defaults: { interval: 30 },
      schema: {
        properties: { interval: { type: "number", minimum: 0.5 } },
        required: ["interval"],
      },
    },
  },
});
function backend({ uninstall = true, pairingFailure = false } = {}) {
  const posts: { path: string; body: Record<string, unknown> }[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), "http://localhost");
      if (init?.method === "POST") {
        posts.push({
          path: url.pathname,
          body: init.body ? JSON.parse(String(init.body)) : {},
        });
        if (url.pathname.endsWith("/pairing-claims"))
          return pairingFailure
            ? Response.json(
                { error: { message: "Pairing code expired" } },
                { status: 409 },
              )
            : Response.json({
                id: "paired",
                name: "New robot",
                organization_id: "org",
              });
        if (url.pathname.endsWith("/credentials"))
          return Response.json({
            credential: { id: "new" },
            secret: "one-time-secret",
          });
        if (url.pathname.endsWith("/enrollment-tokens"))
          return Response.json({ token: "one-time-enrollment" });
        return Response.json({
          operation_id: "operation",
          installation_id: "binding-b",
        });
      }
      if (url.pathname.endsWith("/app-operations/operation"))
        return Response.json({
          id: "operation",
          installation_id: "binding-b",
          organization_id: "org",
          state: "queued",
          phase: "queued",
        });
      if (url.pathname.endsWith("/robots"))
        return Response.json({
          items: [
            {
              id: "unregistered",
              name: "Unregistered Robot",
              enrolled_at: null,
            },
          ],
        });
      if (url.pathname.endsWith("/app-status"))
        return Response.json({
          connectivity: "online",
          supports_app_uninstall: uninstall,
        });
      if (url.pathname.endsWith("/app-compatibility"))
        return Response.json({ status: "compatible" });
      if (url.pathname.endsWith("/configuration-revisions"))
        return Response.json({
          items: [
            { id: "c0", revision_id: "v0", values: { interval: 20 } },
            { id: "c1", revision_id: "v1", values: { interval: 30 } },
          ],
        });
      if (url.pathname.endsWith("/credentials"))
        return Response.json({
          items: [
            {
              id: "credential-b",
              created_at: "2026-11-25T08:00:00Z",
              expires_at: null,
              revoked_at: null,
            },
          ],
        });
      if (url.pathname.endsWith("/robot-b"))
        return Response.json({
          id: "robot-b",
          organization_id: "org",
          name: "Robot B",
        });
      return Response.json({ items: [] });
    }),
  );
  return posts;
}
function Management() {
  return (
    <AppReleaseManagement
      app={app}
      installation={installation}
      robot={{ id: "robot-b", organization_id: "org", name: "Robot B" }}
      revisions={[
        revision("v0", "0.9"),
        revision("v1", "1.0"),
        revision("v2", "2.0"),
      ]}
      runs={[]}
      token={token}
      canManage
      onChanged={() => {}}
    />
  );
}

test("update review targets selected installation and preserves explicit float configuration", async () => {
  const posts = backend();
  window.history.replaceState(
    null,
    "",
    "/apps/camera/overview?workspace=org&installation=binding-b&manage=update",
  );
  render(<Management />);
  const input = await screen.findByLabelText("interval");
  await userEvent.clear(input);
  await userEvent.type(input, "2.5");
  const submit = screen.getByRole("button", { name: "Apply update" });
  await waitFor(() => expect(submit).toHaveProperty("disabled", false));
  await userEvent.click(submit);
  await waitFor(() => expect(posts).toHaveLength(1));
  expect(posts[0]).toEqual({
    path: "/api/v1/app-installations/binding-b/update",
    body: {
      expected_revision_id: "v1",
      expected_configuration_revision_id: "c1",
      revision_id: "v2",
      configuration: { interval: 2.5 },
      share_id: null,
      runtime_settings: null,
    },
  });
  expect(window.location.search).toContain("operation=operation");
});

test("uninstall confirmation names robot and retains history without touching other installations", async () => {
  const posts = backend();
  window.history.replaceState(
    null,
    "",
    "/apps/camera/overview?workspace=org&installation=binding-b&manage=uninstall",
  );
  render(<Management />);
  expect(
    await screen.findByRole("dialog", {
      name: "Uninstall Camera Snapshot Loop on Robot B",
    }),
  ).toBeTruthy();
  expect(screen.getByText(/Published files, run history/)).toBeTruthy();
  const submit = screen.getByRole("button", { name: "Uninstall from Robot B" });
  await waitFor(() => expect(submit).toHaveProperty("disabled", false));
  await userEvent.click(submit);
  await waitFor(() => expect(posts).toHaveLength(1));
  expect(posts[0]).toEqual({
    path: "/api/v1/app-installations/binding-b/uninstall",
    body: {
      expected_revision_id: "v1",
      expected_configuration_revision_id: "c1",
    },
  });
});

test("uninstall remains unavailable when current daemon has not negotiated support", async () => {
  backend({ uninstall: false });
  render(<Management />);
  expect(screen.getByRole("button", { name: "Uninstall" })).toHaveProperty(
    "disabled",
    true,
  );
});

test("credentials route rotates and revokes exact robot credentials and dismisses one-time secret", async () => {
  const posts = backend();
  vi.spyOn(window, "confirm").mockReturnValue(true);
  render(
    <RobotAccessPage
      organizationId="org"
      robotId="robot-b"
      workspaceRole="owner"
    />,
  );
  await userEvent.click(
    await screen.findByRole("button", { name: "Rotate credential" }),
  );
  expect(await screen.findByTestId("one-time-secret")).toHaveProperty(
    "textContent",
    "one-time-secret",
  );
  await userEvent.click(screen.getByRole("button", { name: "Dismiss secret" }));
  expect(screen.queryByTestId("one-time-secret")).toBeNull();
  await userEvent.click(screen.getByRole("button", { name: "Revoke" }));
  await waitFor(() => expect(posts).toHaveLength(2));
  expect(posts.map((item) => item.path)).toEqual([
    "/api/v1/robots/robot-b/credentials",
    "/api/v1/robots/robot-b/credentials/credential-b/revoke",
  ]);
});

test("non-owner cannot issue credentials or enrollment requests", async () => {
  const posts = backend();
  render(
    <RobotAccessPage
      organizationId="org"
      robotId="robot-b"
      workspaceRole="operator"
    />,
  );
  expect(
    screen.queryByRole("button", { name: "Rotate credential" }),
  ).toBeNull();
  expect(
    screen.queryByRole("button", { name: "Issue enrollment token" }),
  ).toBeNull();
  expect(posts).toHaveLength(0);
});

test("owner enrolls a new robot and an existing unregistered target without another workspace picker", async () => {
  const posts = backend();
  render(<RobotAccessPage organizationId="org" workspaceRole="owner" />);
  await userEvent.type(screen.getByLabelText("Pairing code"), "PAIR-CODE");
  await userEvent.type(screen.getByLabelText("Robot name"), "New robot");
  await userEvent.click(screen.getByRole("radio", { name: "Base101 image" }));
  await userEvent.click(screen.getByRole("button", { name: "Connect robot" }));
  await waitFor(() => expect(posts).toHaveLength(1));
  expect(posts[0]).toEqual({
    path: "/api/v1/organizations/org/pairing-claims",
    body: { code: "PAIR-CODE", name: "New robot", appearance: "base101" },
  });
  expect(window.location.pathname).toBe("/robots/paired");
  await userEvent.type(screen.getByLabelText("Pairing code"), "SECOND-CODE");
  await userEvent.selectOptions(
    await screen.findByLabelText("Pair with"),
    "unregistered",
  );
  await userEvent.click(screen.getByRole("button", { name: "Connect robot" }));
  await waitFor(() => expect(posts).toHaveLength(2));
  expect(posts[1].body).toEqual({
    code: "SECOND-CODE",
    robot_id: "unregistered",
    appearance: "bot",
  });
  expect(screen.queryByLabelText("Organization")).toBeNull();
});

test("failed pairing keeps the code editable and reports the server refusal", async () => {
  backend({ pairingFailure: true });
  render(<RobotAccessPage organizationId="org" workspaceRole="owner" />);
  await userEvent.type(screen.getByLabelText("Pairing code"), "EXPIRED");
  await userEvent.type(screen.getByLabelText("Robot name"), "New robot");
  await userEvent.click(screen.getByRole("button", { name: "Connect robot" }));
  expect(await screen.findByRole("alert")).toHaveProperty(
    "textContent",
    expect.stringContaining("Pairing code expired"),
  );
  expect(screen.getByLabelText("Pairing code")).toHaveProperty(
    "value",
    "EXPIRED",
  );
});

test("enrollment token is shown once on the exact robot access route", async () => {
  const posts = backend();
  render(
    <RobotAccessPage
      organizationId="org"
      robotId="robot-b"
      workspaceRole="owner"
    />,
  );
  await userEvent.click(
    await screen.findByRole("button", { name: "Issue enrollment token" }),
  );
  expect(await screen.findByTestId("one-time-secret")).toHaveProperty(
    "textContent",
    "one-time-enrollment",
  );
  expect(posts[0].path).toBe("/api/v1/robots/robot-b/enrollment-tokens");
  await userEvent.click(screen.getByRole("button", { name: "Dismiss secret" }));
  expect(screen.queryByTestId("one-time-secret")).toBeNull();
});
