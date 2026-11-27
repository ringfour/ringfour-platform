import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, expect, test } from "vitest";
import { RobotCapabilities } from "@/features/robots/RobotCapabilities";
import type { RobotAppStatus } from "@/lib/robot-status";

afterEach(cleanup);
const observation = {
  observed_at: "2026-11-25T10:00:00Z",
  source: {
    kind: "agent_welcome",
    profile: { name: "base", model: "simulator", version: "1" },
  },
  capabilities: ["cameras", "imu"],
  instances: { cameras: ["base"] },
};
const status: RobotAppStatus = {
  connectivity: "online",
  support: "supported",
  last_seen_at: observation.observed_at,
  received_at: observation.observed_at,
  inventory: {
    state: "available",
    attempted_at: observation.observed_at,
    current: observation,
  },
};
test("readable capabilities attach real instance names without inventing absent capabilities", () => {
  render(<RobotCapabilities status={status} />);
  expect(screen.getByText("Cameras")).toBeTruthy();
  expect(screen.getByText("Base camera")).toBeTruthy();
  expect(screen.getByText("IMU")).toBeTruthy();
  expect(screen.queryByText("LiDAR")).toBeNull();
  expect(screen.queryByRole("status")).toBeNull();
});
test("unavailable and stale inventory are distinct from a confirmed empty inventory", () => {
  const { rerender } = render(
    <RobotCapabilities status={{ ...status, inventory: null }} />,
  );
  expect(screen.getByRole("status").textContent).toContain(
    "Inventory unavailable",
  );
  expect(
    screen.queryByText("No capabilities reported in this inventory."),
  ).toBeNull();
  rerender(
    <RobotCapabilities
      status={{
        ...status,
        connectivity: "offline",
        inventory: {
          state: "unavailable",
          attempted_at: observation.observed_at,
          last_successful: observation,
          reason: "agent_unavailable",
        },
      }}
    />,
  );
  expect(screen.getByRole("status").textContent).toContain(
    "Last known inventory",
  );
  expect(screen.getByText("Base camera")).toBeTruthy();
  rerender(
    <RobotCapabilities
      status={{
        ...status,
        inventory: {
          state: "available",
          attempted_at: observation.observed_at,
          current: { ...observation, capabilities: [] },
        },
      }}
    />,
  );
  expect(
    screen.getByText("No capabilities reported in this inventory."),
  ).toBeTruthy();
  expect(screen.queryByRole("status")).toBeNull();
});
