import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";
import { RunTimeline } from "./RunTimeline";
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  window.history.replaceState(null, "", "/");
});
test("keeps simultaneous executions selectable and exposes the recorded event times", async () => {
  const end = Date.parse("2026-11-21T12:00:00Z");
  window.history.replaceState(
    null,
    "",
    "/runs?view=timeline&hours=6&until=" + end,
  );
  const robot = { id: "r", name: "Assembly robot" };
  const base = {
    robot,
    created_at: "2026-11-21T05:00:00Z",
    updated_at: "2026-11-21T11:00:00Z",
    app: "Inspection",
    version: 3,
  };
  vi.stubGlobal(
    "fetch",
    vi.fn(async () =>
      Response.json({
        items: [
          {
            event_id: "evidence",
            execution_id: "first",
            robot_id: "r",
            type: "mission.logbook.entry",
            payload: {
              message: "Inspection photo captured",
              evidence: ["00000000-0000-4000-8000-000000000001"],
            },
            occurred_at: "2026-11-21T08:10:00Z",
            received_at: "2026-11-21T08:10:10Z",
          },
          {
            event_id: "event",
            execution_id: "first",
            robot_id: "r",
            type: "human.interaction.requested",
            payload: { message: "Inspect weld?" },
            occurred_at: "2026-11-21T10:14:30Z",
            received_at: "2026-11-21T10:18:40Z",
          },
        ],
        next_cursor: null,
      }),
    ),
  );
  render(
    <RunTimeline
      workspace="org"
      robots={[robot]}
      token={async () => "test"}
      runs={[
        { ...base, id: "first", state: "succeeded" },
        { ...base, id: "second", state: "failed" },
        {
          ...base,
          id: "third",
          app: "Diagnostics",
          state: "succeeded",
          created_at: "2026-11-21T11:10:00Z",
          updated_at: "2026-11-21T11:30:00Z",
        },
      ]}
    />,
  );
  const first = screen.getByRole("button", {
    name: /Inspection v3 · succeeded/,
  });
  const other = screen.getByRole("button", { name: /Inspection v3 · failed/ });
  const differentMission = screen.getByRole("button", {
    name: /Diagnostics v3 · succeeded/,
  });
  expect(first.style.top).not.toEqual(other.style.top);
  expect(first.style.top).not.toEqual(differentMission.style.top);
  fireEvent.focus(first);
  expect((await screen.findByRole("tooltip")).textContent).toContain(
    "Inspection v3",
  );
  fireEvent.blur(first);
  await userEvent.click(first);
  expect(
    screen.getByRole("link", { name: "Open run →" }).getAttribute("href"),
  ).toBe("/runs/first?workspace=org");
  await userEvent.click(
    await screen.findByRole("button", {
      name: "Decision requested: Inspect weld?",
    }),
  );
  expect(
    screen.getByRole("region", { name: "Selected timeline event" }),
  ).toBeTruthy();
  expect(screen.getByRole("heading", { name: "Inspect weld?" })).toBeTruthy();
  expect(
    document.querySelector('time[datetime="2026-11-21T10:14:30Z"]'),
  ).toBeTruthy();
  expect(
    document.querySelector('time[datetime="2026-11-21T10:18:40Z"]'),
  ).toBeTruthy();
  await userEvent.click(
    screen.getByRole("button", { name: "Close event card" }),
  );
  expect(
    screen.queryByRole("region", { name: "Selected timeline event" }),
  ).toBeNull();
  await userEvent.click(
    screen.getByRole("button", {
      name: "Evidence recorded: Inspection photo captured",
    }),
  );
  expect(
    screen.getByRole("heading", { name: "Inspection photo captured" }),
  ).toBeTruthy();
});
