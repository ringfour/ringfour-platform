import {
  cleanup,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";
import { LogbookPage } from "./LogbookPage";

vi.mock("@auth0/auth0-react", () => ({
  useAuth0: () => ({ getAccessTokenSilently: token }),
}));
const token = vi.fn(async () => "token");
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const event = (
  id: string,
  executionId: string | null,
  message: string,
  robotId = "robot-a",
) => ({
  event_id: id,
  robot_id: robotId,
  local_sequence: 1,
  operation_id: executionId ? "operation" : null,
  execution_id: executionId,
  actor_user_id: executionId ? "user" : null,
  type: executionId ? "mission.logbook.entry" : "agent.motion.event",
  occurred_at: "2026-11-13T12:00:00Z",
  received_at: "2026-11-13T12:00:01Z",
  payload: executionId
    ? { message, fields: { station: "B" } }
    : { event_type: message, owner: "unix:uid:1000" },
});

function mockBackend() {
  return vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(String(input), "http://console.test");
    let items: unknown[] = [];
    let nextCursor: string | null = null;
    if (url.pathname === "/api/v1/me")
      return new Response(JSON.stringify({ user: { id: "user-a" } }), {
        status: 200,
      });
    if (url.pathname === "/api/v1/organizations")
      items = [{ id: "org", name: "Acme" }];
    else if (url.pathname === "/api/v1/organizations/org/members")
      items = [
        { user_id: "user-a", role: "developer" },
        { user_id: "user-b", role: "operator" },
      ];
    else if (url.pathname === "/api/v1/organizations/org/missions")
      items = [
        { id: "mission-a", name: "Inspection" },
        { id: "mission-b", name: "Delivery" },
      ];
    else if (url.pathname === "/api/v1/organizations/org/robots")
      items = [
        { id: "robot-a", name: "base101" },
        { id: "robot-b", name: "ducky" },
      ];
    else if (url.pathname === "/api/v1/missions/mission-a/revisions")
      items = [
        { id: "revision-a1", version: 1 },
        { id: "revision-a2", version: 2 },
      ];
    else if (url.pathname === "/api/v1/missions/mission-b/revisions")
      items = [{ id: "revision-b", version: 1 }];
    else if (url.pathname === "/api/v1/robots/robot-a/executions")
      items = [
        {
          id: "run-a",
          actor_user_id: "user-a",
          mission_revision_id: "revision-a1",
          state: "failed",
          created_at: "2026-11-13T12:00:00Z",
        },
        {
          id: "run-b",
          actor_user_id: "user-b",
          mission_revision_id: "revision-b",
          state: "succeeded",
          created_at: "2026-11-13T13:00:00Z",
        },
        {
          id: "run-direct",
          actor_user_id: null,
          mission_revision_id: null,
          state: "succeeded",
          created_at: "2026-11-13T14:00:00Z",
        },
      ];
    else if (url.pathname === "/api/v1/robots/robot-b/executions")
      items = [
        {
          id: "run-ducky",
          actor_user_id: "user-a",
          mission_revision_id: "revision-a2",
          state: "succeeded",
          created_at: "2026-11-13T14:00:00Z",
        },
      ];
    else if (url.pathname === "/api/v1/organizations/org/events") {
      const execution = url.searchParams.get("execution_id");
      if (execution === "run-a") {
        if (url.searchParams.get("cursor"))
          items = [event("first", "run-a", "Inspection started")];
        else {
          items = [
            {
              ...event("second", "run-a", "Inspection skipped"),
              occurred_at: "2026-11-13T12:01:00Z",
            },
          ];
          nextCursor = "next-page";
        }
      } else if (execution === "run-b")
        items = [event("delivery", "run-b", "Delivery finished")];
      else if (execution === "run-direct")
        items = [event("direct", "run-direct", "Direct run")];
      else if (execution === "run-ducky")
        items = [event("ducky", "run-ducky", "Ducky log", "robot-b")];
      else
        items = [
          event("first", "run-a", "Inspection started"),
          event("delivery", "run-b", "Delivery finished"),
          event("direct", "run-direct", "Direct run"),
          event("motion", null, "motion.guard_tripped"),
          event("motion-ok", null, "motion.completed"),
          event("motion-gap", null, "motion.history_incomplete"),
          event("motion-unknown", null, "motion.some_new_outcome"),
          event("dev", "unknown-dev-run", "Development note"),
        ];
    } else if (
      url.pathname.startsWith("/api/v1/executions/") &&
      url.pathname.endsWith("/output")
    ) {
      const text =
        url.searchParams.get("stream") === "stdout" ? "mission stdout" : "";
      return new Response(
        JSON.stringify({
          data_base64: btoa(text),
          next_offset: text.length,
          truncated: false,
        }),
        { status: 200 },
      );
    } else throw new Error(`Unexpected API request: ${url}`);
    return new Response(JSON.stringify({ items, next_cursor: nextCursor }), {
      status: 200,
    });
  });
}

const renderPage = () =>
  render(
    <LogbookPage
      preferredOrganizationId="org"
      onOrganizationChange={() => {}}
    />,
  );

test("filters executions using the labels and shows a chronological expandable timeline", async () => {
  const fetcher = mockBackend();
  vi.stubGlobal("fetch", fetcher);
  renderPage();
  const sidebar = await screen.findByRole("complementary", {
    name: "Logbook executions",
  });
  await within(sidebar).findByRole("button", { name: "Open execution run-a" });
  expect(
    within(sidebar).getAllByText(/as: developer \(current\)/),
  ).toHaveLength(2);
  expect(within(sidebar).getByText(/as: operator \(current\)/)).toBeTruthy();
  expect(within(sidebar).getByText(/as: unknown \(current\)/)).toBeTruthy();
  await userEvent.click(
    within(sidebar).getAllByRole("button", {
      name: "Filter by mission Inspection",
    })[0],
  );
  expect((screen.getByLabelText("Mission") as HTMLSelectElement).value).toBe(
    "mission-a",
  );
  expect(
    within(sidebar).queryByRole("button", { name: "Open execution run-b" }),
  ).toBeNull();
  await userEvent.click(
    within(sidebar).getByRole("button", { name: "Filter by robot base101" }),
  );
  expect(
    within(sidebar).queryByRole("button", { name: "Open execution run-ducky" }),
  ).toBeNull();
  await userEvent.click(
    within(sidebar).getByRole("button", { name: "Filter by human You" }),
  );
  expect((screen.getByLabelText("Human") as HTMLSelectElement).value).toBe(
    "user-a",
  );
  for (const filter of ["mission", "robot", "human"]) {
    const button = screen.getByRole("button", {
      name: `Clear ${filter} filter`,
    });
    expect(button.hasAttribute("disabled")).toBe(false);
  }
  const failed = within(sidebar).getByText("failed");
  expect(failed.getAttribute("data-state")).toBe("failed");
  await userEvent.click(
    within(sidebar).getByRole("button", { name: "Open execution run-a" }),
  );
  const timeline = await screen.findByRole("region", {
    name: "Logbook timeline",
  });
  expect(await within(timeline).findByText("Inspection started")).toBeTruthy();
  expect(within(timeline).getByText("Inspection skipped")).toBeTruthy();
  expect(
    within(timeline)
      .getAllByText(/^Inspection (started|skipped)$/)
      .map((el) => el.textContent),
  ).toEqual(["Inspection started", "Inspection skipped"]);
  await userEvent.click(within(timeline).getByText("Inspection skipped"));
  expect(within(timeline).getAllByText(/"station": "B"/)).toHaveLength(2);
  await userEvent.click(screen.getByText("Execution output"));
  expect(await screen.findByText("mission stdout")).toBeTruthy();
  await waitFor(() =>
    expect(
      fetcher.mock.calls.some(([input]) =>
        String(input).includes(
          "robot_id=robot-a&execution_id=run-a&cursor=next-page",
        ),
      ),
    ).toBe(true),
  );
  await userEvent.selectOptions(screen.getByLabelText("Robot"), "robot-b");
  expect(screen.queryByText("Inspection skipped")).toBeNull();
  expect(
    within(sidebar).getByRole("button", { name: "Open execution run-ducky" }),
  ).toBeTruthy();
  expect(
    within(sidebar).getByText("succeeded").getAttribute("data-state"),
  ).toBe("succeeded");
  for (const filter of ["mission", "robot", "human"]) {
    await userEvent.click(
      screen.getByRole("button", { name: `Clear ${filter} filter` }),
    );
    expect(
      (
        screen.getByLabelText(
          filter[0].toUpperCase() + filter.slice(1),
        ) as HTMLSelectElement
      ).value,
    ).toBe("");
    expect(
      screen
        .getByRole("button", { name: `Clear ${filter} filter` })
        .hasAttribute("disabled"),
    ).toBe(true);
  }
  expect(
    within(sidebar).getByRole("button", { name: "Open execution run-b" }),
  ).toBeTruthy();
});

test("keeps unlinked Agent events and direct runs without guessing attribution", async () => {
  vi.stubGlobal("fetch", mockBackend());
  renderPage();
  const sidebar = await screen.findByRole("complementary", {
    name: "Logbook executions",
  });
  await within(sidebar).findByRole("button", {
    name: "Open execution run-direct",
  });
  await userEvent.click(
    within(sidebar).getByRole("button", {
      name: /Unlinked robot events base101/,
    }),
  );
  const danger = await screen.findByText("motion.guard_tripped");
  expect(danger.closest("details")?.getAttribute("data-tone")).toBe("danger");
  expect(
    screen
      .getByText("motion.completed")
      .closest("details")
      ?.getAttribute("data-tone"),
  ).toBe("success");
  expect(
    screen
      .getByText("motion.history_incomplete")
      .closest("details")
      ?.getAttribute("data-tone"),
  ).toBe("warning");
  expect(
    screen
      .getByText("motion.some_new_outcome")
      .closest("details")
      ?.getAttribute("data-tone"),
  ).toBe("neutral");
  await userEvent.click(danger);
  expect(
    within(danger.closest("details") as HTMLElement).getByText(
      /"owner": "unix:uid:1000"/,
    ),
  ).toBeTruthy();
  expect(screen.getByText("Development note")).toBeTruthy();
  expect(screen.queryByText("Inspection started")).toBeNull();
  await userEvent.click(
    within(sidebar).getByRole("button", {
      name: "Filter by mission Direct run",
    }),
  );
  expect(
    within(sidebar).getByRole("button", { name: "Open execution run-direct" }),
  ).toBeTruthy();
  expect(
    within(sidebar).queryByRole("button", { name: /Unlinked robot events/ }),
  ).toBeNull();
  expect(screen.queryByText("motion.guard_tripped")).toBeNull();
});

test("shows empty execution lists", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn(
      async (input: RequestInfo | URL) =>
        new Response(
          JSON.stringify({
            items: String(input).endsWith("/organizations")
              ? [{ id: "org", name: "Acme" }]
              : [],
            next_cursor: null,
          }),
          { status: 200 },
        ),
    ),
  );
  renderPage();
  expect(
    await screen.findByText("No executions match these filters."),
  ).toBeTruthy();
});

test("reports loading errors rather than displaying an incomplete execution list", async () => {
  const backend = mockBackend();
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).endsWith("/robots/robot-b/executions"))
        return new Response(
          JSON.stringify({
            error: { code: "failed", message: "Could not load runs" },
          }),
          { status: 503 },
        );
      return backend(input);
    }),
  );
  renderPage();
  expect((await screen.findByRole("alert")).textContent).toContain(
    "Could not load runs",
  );
});
