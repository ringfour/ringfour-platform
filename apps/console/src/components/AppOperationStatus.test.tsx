import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, expect, test, vi } from "vitest";
import { AppOperationStatus } from "./AppOperationStatus";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

test("refreshes the owning view once when an operation reaches terminal state", async () => {
  const onTerminal = vi.fn();
  vi.stubGlobal(
    "fetch",
    vi.fn(async () =>
      Response.json({
        id: "operation-1",
        organization_id: "org",
        robot_id: "robot",
        installation_id: "installation",
        run_id: "run",
        kind: "stop",
        state: "succeeded",
        phase: "stopped",
        requested_at: "2026-11-25T00:00:00Z",
        acknowledged_at: null,
        observed_at: null,
        completed_at: "2026-11-25T00:00:01Z",
        failure: null,
      }),
    ),
  );

  const view = render(
    <AppOperationStatus
      operationId="operation-1"
      token={async () => "token"}
      onTerminal={onTerminal}
    />,
  );
  expect(await screen.findByText("Stopped")).toBeTruthy();
  view.rerender(
    <AppOperationStatus
      operationId="operation-1"
      token={async () => "token"}
      onTerminal={onTerminal}
    />,
  );
  expect(onTerminal).toHaveBeenCalledTimes(1);
});

test("a stale operation from another selected installation never becomes success", async () => {
  const onTerminal = vi.fn();
  vi.stubGlobal(
    "fetch",
    vi.fn(async () =>
      Response.json({
        id: "operation",
        installation_id: "robot-a-install",
        organization_id: "org",
        state: "succeeded",
        phase: "applied",
      }),
    ),
  );
  render(
    <AppOperationStatus
      operationId="operation"
      installationId="robot-b-install"
      organizationId="org"
      token={async () => "token"}
      onTerminal={onTerminal}
    />,
  );
  expect(await screen.findByRole("alert")).toHaveProperty(
    "textContent",
    expect.stringContaining("unavailable in the selected installation"),
  );
  expect(onTerminal).not.toHaveBeenCalled();
});
