import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";
import { AppLiveSettings } from "@/features/apps/AppLiveSettings";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});
test("pending and rejected requests retain the confirmed value and use the exact revision/run binding", async () => {
  let phase = "idle";
  const posts: unknown[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = new URL(String(input), "http://localhost").pathname;
      if (init?.method === "POST") {
        posts.push(JSON.parse(String(init.body)));
        phase = "pending";
        return Response.json(
          { state: "pending", application_id: "application", run_id: "run" },
          { status: 202 },
        );
      }
      if (path.endsWith("/app-logbook"))
        return Response.json({
          items:
            phase === "rejected"
              ? [
                  {
                    event_id: "event",
                    installation_id: "installation-b",
                    type: "app.settings.rejected",
                    occurred_at: "2026-11-25T10:00:00Z",
                    payload: { reason: "Value refused" },
                  },
                ]
              : [],
          next_cursor: null,
        });
      return Response.json({
        installation_id: "installation-b",
        support: "supported",
        runtime_settings: {
          defaults: { frequency: 2 },
          schema: {
            properties: { frequency: { type: "number", minimum: 0.5 } },
            required: ["frequency"],
          },
        },
        selected_for_next_start: { id: "selected", values: { frequency: 2 } },
        active_run_id: "run",
        last_applied: {
          id: "applied",
          values: { frequency: 2 },
          created_at: "2026-11-25T10:00:00Z",
        },
        pending_application:
          phase === "pending"
            ? {
                application_id: "application",
                values: { frequency: 3.5 },
                state: "pending",
                delivery_state: "sent",
              }
            : null,
      });
    }),
  );
  render(
    <AppLiveSettings
      installationId="installation-b"
      organizationId="org"
      robotName="Robot B"
      token={async () => "token"}
      canManage
      onDirtyChange={vi.fn()}
    />,
  );
  const field = await screen.findByLabelText("frequency · required");
  await userEvent.clear(field);
  await userEvent.type(field, "3.5");
  await userEvent.click(
    screen.getByRole("button", { name: "Apply live settings" }),
  );
  expect(
    await screen.findByText("Requested: 3.5 · Awaiting App confirmation"),
  ).toBeTruthy();
  expect(screen.getByText("Confirmed: 2")).toBeTruthy();
  expect(posts).toEqual([
    {
      values: { frequency: 3.5 },
      expected_selected_revision_id: "selected",
      expected_applied_revision_id: "applied",
      expected_run_id: "run",
    },
  ]);
  phase = "rejected";
  await userEvent.click(screen.getByRole("button", { name: "Refresh status" }));
  expect(
    await screen.findByText("Last settings request rejected: Value refused"),
  ).toBeTruthy();
  expect(screen.getByText("Confirmed: 2")).toBeTruthy();
});
