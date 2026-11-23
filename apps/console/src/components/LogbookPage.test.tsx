import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";

const mocks = vi.hoisted(() => ({
  apiFetch: vi.fn(),
  apiFetchAll: vi.fn(),
  getAccessTokenSilently: vi.fn(async () => "access-token"),
}));
vi.mock("@/lib/api", () => ({
  apiFetch: mocks.apiFetch,
  apiFetchAll: mocks.apiFetchAll,
}));
vi.mock("@auth0/auth0-react", () => ({
  useAuth0: () => ({ getAccessTokenSilently: mocks.getAccessTokenSilently }),
}));

import { LogbookPage } from "./LogbookPage";

const orgId = "org-1";
const robotId = "robot-1";
const executionId = "execution-1";
const linkedId = "artifact-1";
const standaloneId = "artifact-2";
const photo = (artifact_id: string, path: string) => ({
  artifact_id,
  path,
  size: 512,
  sha256: "hash",
  content_type: "image/jpeg",
  state: "available",
});
const json = (body: unknown) => new Response(JSON.stringify(body));
const originalCreateObjectURL = Object.getOwnPropertyDescriptor(
  URL,
  "createObjectURL",
);
const originalRevokeObjectURL = Object.getOwnPropertyDescriptor(
  URL,
  "revokeObjectURL",
);

afterEach(() => {
  cleanup();
  vi.resetAllMocks();
  vi.unstubAllGlobals();
  if (originalCreateObjectURL)
    Object.defineProperty(URL, "createObjectURL", originalCreateObjectURL);
  else Reflect.deleteProperty(URL, "createObjectURL");
  if (originalRevokeObjectURL)
    Object.defineProperty(URL, "revokeObjectURL", originalRevokeObjectURL);
  else Reflect.deleteProperty(URL, "revokeObjectURL");
});

test("shows linked and standalone photo evidence in the timeline even when output fails", async () => {
  vi.stubGlobal("scrollTo", vi.fn());
  Object.defineProperties(URL, {
    createObjectURL: {
      configurable: true,
      value: vi.fn(() => "blob:mission-photo"),
    },
    revokeObjectURL: { configurable: true, value: vi.fn() },
  });
  mocks.apiFetchAll.mockImplementation(
    async (_getToken: unknown, path: string) => {
      if (path === "/v1/organizations")
        return [{ id: orgId, name: "Workspace" }];
      if (path.endsWith("/missions")) return [];
      if (path.endsWith("/robots")) return [{ id: robotId, name: "Base 101" }];
      if (path.endsWith("/members")) return [];
      if (path.endsWith("/revisions")) return [];
      if (path.endsWith("/executions")) {
        return [
          {
            id: executionId,
            mission_revision_id: null,
            actor_user_id: null,
            state: "succeeded",
            created_at: "2026-11-16T10:00:00Z",
          },
        ];
      }
      if (path.includes("/events?")) {
        return [
          {
            event_id: "state-event-1",
            robot_id: robotId,
            local_sequence: 1,
            operation_id: executionId,
            execution_id: null,
            actor_user_id: null,
            type: "device.operation.state_changed",
            occurred_at: "2026-11-16T10:00:30Z",
            received_at: "2026-11-16T10:00:31Z",
            payload: { state: "running" },
          },
          {
            event_id: "mission-started-1",
            robot_id: robotId,
            local_sequence: null,
            operation_id: executionId,
            execution_id: executionId,
            actor_user_id: "mission-actor-id",
            type: "mission.started",
            occurred_at: "2026-11-16T10:00:40Z",
            received_at: "2026-11-16T10:00:40Z",
            payload: {
              state: "running",
              revision_id: "revision-1",
              mission_revision_id: null,
            },
          },
          {
            event_id: "event-1",
            robot_id: robotId,
            local_sequence: 2,
            operation_id: executionId,
            execution_id: executionId,
            actor_user_id: null,
            type: "mission.logbook.entry",
            occurred_at: "2026-11-16T10:01:00Z",
            received_at: "2026-11-16T10:01:01Z",
            payload: { message: "Photo captured", evidence: [linkedId] },
          },
          {
            event_id: "answer-event-1",
            robot_id: robotId,
            local_sequence: null,
            operation_id: executionId,
            execution_id: executionId,
            actor_user_id: "operator-user-id",
            type: "human.interaction.answered",
            occurred_at: "2026-11-16T10:02:00Z",
            received_at: "2026-11-16T10:02:01Z",
            payload: {
              message: "Human interaction answered",
              interaction_id: "interaction-1",
              prompt: "Continue?",
              selected_label: "Continue inspection",
              outcome: "continue",
              response_id: "response-1",
            },
          },
        ];
      }
      throw new Error(`Unexpected list request: ${path}`);
    },
  );
  mocks.apiFetch.mockImplementation(
    async (_getToken: unknown, path: string) => {
      if (path === "/v1/me") return json({ user: { id: "user-1" } });
      if (path.endsWith("/artifacts")) {
        return json({
          artifacts: [
            photo(linkedId, "photos/linked.jpg"),
            photo(standaloneId, "photos/extra.jpg"),
          ],
        });
      }
      if (path.includes("/artifacts/content"))
        return new Response(new Blob(["photo"]));
      if (path.includes("/output")) throw new Error("Output not ready");
      throw new Error(`Unexpected API request: ${path}`);
    },
  );

  render(
    <LogbookPage preferredOrganizationId="" onOrganizationChange={() => {}} />,
  );
  await userEvent.click(screen.getByRole("button", { name: "Finished" }));
  await userEvent.click(
    await screen.findByRole("link", { name: /Open Direct run execution/ }),
  );

  expect(await screen.findByText("Mission running")).toBeTruthy();

  const timeline = screen.getByRole("region", { name: "Logbook timeline" });
  expect(
    await within(timeline).findAllByText("device.operation.state_changed"),
  ).toHaveLength(2);
  expect(await within(timeline).findByText("Photo captured")).toBeTruthy();
  await userEvent.click(
    await within(timeline).findByText("Human interaction answered"),
  );
  expect(await within(timeline).findByText("Continue inspection")).toBeTruthy();
  expect(await within(timeline).findByText("operator-user-id")).toBeTruthy();
  expect(await within(timeline).findAllByText("Hosted event")).toHaveLength(2);
  expect(
    await within(timeline).findByRole("img", {
      name: "Mission artifact photos/linked.jpg",
    }),
  ).toBeTruthy();
  expect(
    await within(timeline).findByText("Photo evidence · extra.jpg"),
  ).toBeTruthy();
  await userEvent.click(screen.getByRole("button", { name: "Output" }));
  expect(
    await screen.findByText("Output unavailable: Output not ready"),
  ).toBeTruthy();
  expect(mocks.apiFetchAll).toHaveBeenCalledWith(
    expect.any(Function),
    `/v1/organizations/${orgId}/events?robot_id=${robotId}&execution_id=${executionId}`,
  );
});
