import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useEffect } from "react";
import { afterEach, expect, test, vi } from "vitest";
import { AppLogbookPage } from "@/features/logbook/AppLogbookPage";
import type { AppRun } from "@/lib/apps";
import { followStudioLink, useLocation } from "@/lib/location";
import { StudioShell } from "@/views/StudioShell";

const token = async () => "test";
vi.mock("@auth0/auth0-react", () => ({
  useAuth0: () => ({ getAccessTokenSilently: token }),
}));
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  window.history.replaceState(null, "", "/");
});
const stamp = new Date(Date.now() - 60000).toISOString();
function run(id: string, state: AppRun["state"]): AppRun {
  return {
    id,
    organization_id: "org",
    robot_id: "robot-b",
    installation_id: "installation-b",
    start_operation_id: "operation",
    snapshot: {
      app_id: "camera",
      revision_id: "revision",
      publisher_organization_id: "publisher",
      configuration_revision_id: "configuration",
      configuration: {},
      image: "registry/app@sha256:abc",
    },
    state,
    requested_by: "operator",
    requested_at: stamp,
    started_at: stamp,
    observed_at: stamp,
    ended_at: state === "running" ? null : stamp,
    exit_code: state === "failed" ? 1 : 0,
    failure:
      state === "failed"
        ? { code: "failed", message: "Camera unavailable" }
        : null,
  };
}
function backend({
  foreign = false,
  outputFailure = false,
  retry = false,
} = {}) {
  const requests: string[] = [];
  let recordReads = 0;
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), "http://localhost");
      requests.push(url.pathname + url.search);
      if (url.pathname === "/api/v1/app-runs/failed") {
        recordReads++;
        if (retry && recordReads === 2)
          return Response.json(
            { error: { message: "Connection lost" } },
            { status: 503 },
          );
        return Response.json({
          ...run("failed", retry && recordReads < 3 ? "running" : "failed"),
          organization_id: foreign ? "other-org" : "org",
        });
      }
      if (url.pathname.endsWith("/app-runs"))
        return Response.json({
          items: [
            run("failed", "failed"),
            run("active", "running"),
            run("done", "succeeded"),
          ],
          next_cursor: null,
        });
      if (url.pathname.endsWith("/robots"))
        return Response.json({
          items: [{ id: "robot-b", organization_id: "org", name: "Robot B" }],
          next_cursor: null,
        });
      if (url.pathname.endsWith("/members"))
        return Response.json({
          items: [{ user_id: "operator", name: "Operator" }],
          next_cursor: null,
        });
      if (url.pathname.endsWith("/me"))
        return Response.json({ user: { id: "operator" } });
      if (url.pathname === "/api/v1/apps/camera")
        return Response.json({
          id: "camera",
          name: "Camera Check",
          description: "Inspect camera",
        });
      if (url.pathname === "/api/v1/app-revisions/revision")
        return Response.json({
          id: "revision",
          manifest: { release: "1.2.0" },
        });
      if (url.pathname.endsWith("/app-logbook"))
        return Response.json({
          items: [
            {
              event_id: "event",
              organization_id: "org",
              run_id: "failed",
              robot_id: "robot-b",
              type: "app.logbook.entry",
              occurred_at: stamp,
              received_at: stamp,
              actor_user_id: "operator",
              operation_id: null,
              payload: { message: "Camera inspection started" },
            },
            {
              event_id: "wrong-workspace",
              organization_id: "other-org",
              run_id: "failed",
              robot_id: "robot-b",
              type: "app.logbook.entry",
              occurred_at: stamp,
              received_at: stamp,
              payload: { message: "Other workspace event" },
            },
            {
              event_id: "wrong-run",
              organization_id: "org",
              run_id: "other-run",
              robot_id: "robot-b",
              type: "app.logbook.entry",
              occurred_at: stamp,
              received_at: stamp,
              payload: { message: "Other run event" },
            },
          ],
          next_cursor: null,
        });
      if (url.pathname.endsWith("/output"))
        return outputFailure
          ? Response.json(
              { error: { message: "Output unavailable" } },
              { status: 503 },
            )
          : Response.json({
              data_base64: btoa(
                url.searchParams.get("stream") === "stdout"
                  ? "Frame inspected"
                  : "Camera disconnected",
              ),
              next_offset: 16,
              sealed: true,
              truncated: false,
            });
      if (url.pathname.endsWith("/artifacts"))
        return Response.json({ items: [], next_cursor: null });
      if (url.pathname.endsWith("/interactions"))
        return Response.json({ items: [], next_cursor: null });
      throw new Error(`Unexpected endpoint ${url.pathname}`);
    }),
  );
  return requests;
}
function Harness() {
  const location = useLocation();
  useEffect(() => {
    document.addEventListener("click", followStudioLink);
    return () => document.removeEventListener("click", followStudioLink);
  }, []);
  const id = location.pathname.split("/")[2];
  return (
    <StudioShell
      workspaces={[{ id: "org", name: "Workshop", role: "owner" }]}
      workspace="org"
      view="logbook"
    >
      <AppLogbookPage
        key={id ?? "list"}
        organizationId="org"
        runId={id}
        workspaceRole="owner"
      />
    </StudioShell>
  );
}
function open(path = "/runs?workspace=org") {
  window.history.replaceState(null, "", path);
  render(<Harness />);
}
test("opens the exact run from its title and retains its group and installation", async () => {
  backend();
  open();
  await screen.findByText("3 records");
  await userEvent.click(screen.getByRole("link", { name: "Needs attention" }));
  await screen.findByText("1 record");
  expect(window.location.search).toContain("group=Needs+attention");
  expect(
    screen.getByRole("link", { name: "Camera Check" }).getAttribute("href"),
  ).toContain("/runs/failed?");
  await userEvent.click(screen.getByRole("link", { name: "Camera Check" }));
  expect(await screen.findByText("App run record")).toBeTruthy();
  expect(
    screen.getByRole("link", { name: /Back to runs/ }).getAttribute("href"),
  ).toContain("group=Needs+attention");
  expect(
    screen.getByRole("link", { name: "Open App" }).getAttribute("href"),
  ).toContain("installation-b");
});
test("restores numbered timeline and artifact sections with lazy split output", async () => {
  const requests = backend();
  open("/runs/failed?workspace=org");
  await screen.findByRole("heading", { name: "Camera Check" });
  await screen.findAllByText("Camera inspection started");
  expect(screen.getByRole("region", { name: "Logbook timeline" })).toBeTruthy();
  expect(screen.getByRole("region", { name: "Run artifacts" })).toBeTruthy();
  expect(screen.queryByText("Other workspace event")).toBeNull();
  expect(screen.queryByText("Other run event")).toBeNull();
  expect(requests.some((path) => path.includes("/output"))).toBe(false);
  await userEvent.click(screen.getByRole("link", { name: "Output" }));
  expect(await screen.findByText("Frame inspected")).toBeTruthy();
  expect(await screen.findByText("Camera disconnected")).toBeTruthy();
  expect(screen.getByRole("heading", { name: "Standard output" })).toBeTruthy();
  expect(screen.getByRole("heading", { name: "Standard error" })).toBeTruthy();
  expect(
    requests.some((path) =>
      /\/executions\/|\/missions\/|\/organizations\/org\/events/.test(path),
    ),
  ).toBe(false);
});
test("checks run workspace before requesting its output, events or artifacts", async () => {
  const requests = backend({ foreign: true });
  open("/runs/failed?workspace=org&tab=Output");
  expect(await screen.findByRole("alert")).toHaveProperty(
    "textContent",
    expect.stringContaining("unavailable in this workspace"),
  );
  expect(
    requests.some((path) => /\/output|\/artifacts|\/app-logbook/.test(path)),
  ).toBe(false);
});
test("output errors leave the record and timeline usable", async () => {
  backend({ outputFailure: true });
  open("/runs/failed?workspace=org&tab=Output");
  await screen.findByRole("heading", { name: "Camera Check" });
  expect(await screen.findByRole("alert", { name: "" })).toBeTruthy();
  await userEvent.click(screen.getByRole("link", { name: "Overview" }));
  expect(
    await screen.findByRole("region", { name: "Logbook timeline" }),
  ).toBeTruthy();
});
test("active record polling recovers from a failed read without losing the record", async () => {
  backend({ retry: true });
  open("/runs/failed?workspace=org&tab=Output");
  await screen.findByRole("heading", { name: "Camera Check" });
  await waitFor(
    () =>
      expect(screen.getByRole("alert").textContent).toContain(
        "Connection lost",
      ),
    { timeout: 2200 },
  );
  expect(screen.getByRole("heading", { name: "Camera Check" })).toBeTruthy();
  await waitFor(
    () => expect(screen.getByText("Camera unavailable")).toBeTruthy(),
    { timeout: 2200 },
  );
  expect(screen.queryByText(/Connection lost/)).toBeNull();
});

test("visible filters change the URL and clear without losing the run group", async () => {
  backend();
  open();
  await screen.findByText("3 records");
  const groups = screen.getByRole("navigation", { name: "Run groups" });
  const filters = screen.getByRole("region", { name: "Run filters" });
  const runs = screen.getByRole("region", { name: "Runs" });
  expect(runs.contains(groups)).toBe(true);
  expect(
    filters.compareDocumentPosition(groups) & Node.DOCUMENT_POSITION_FOLLOWING,
  ).toBeTruthy();
  expect(
    screen.getByRole("navigation", { name: "Studio toolbar" }).contains(groups),
  ).toBe(false);
  await userEvent.selectOptions(screen.getByLabelText("Status"), "failed");
  await screen.findByText("1 record");
  expect(new URLSearchParams(window.location.search).get("state")).toBe(
    "failed",
  );
  await userEvent.click(screen.getByRole("button", { name: "Clear all" }));
  await screen.findByText("3 records");
  expect(new URLSearchParams(window.location.search).has("state")).toBe(false);
});
test("clipboard refusal is reported without hiding output", async () => {
  backend();
  const user = userEvent.setup();
  vi.spyOn(navigator.clipboard, "writeText").mockRejectedValueOnce(
    new Error("denied"),
  );
  open("/runs/failed?workspace=org&tab=Output");
  await screen.findByText("Frame inspected");
  await user.click(screen.getByRole("button", { name: "Copy stdout" }));
  expect(await screen.findByText("Clipboard unavailable.")).toBeTruthy();
  expect(screen.getByText("Frame inspected")).toBeTruthy();
});
