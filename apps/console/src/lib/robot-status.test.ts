import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { expect, test } from "vitest";
import {
  agentUpdate,
  daemonUpdate,
  inventoryObservation,
  type RobotAppStatus,
} from "./robot-status";

const fixtures = JSON.parse(
  readFileSync(
    resolve(
      process.cwd(),
      "../../packages/contracts/fixtures/device/apps/v1/positive.json",
    ),
    "utf8",
  ),
) as { name: string; value: RobotAppStatus }[];
test("robot details read the exact inventory observation used by App compatibility", () => {
  const status = fixtures.find((item) => item.name === "online_status")!.value;
  const observed = inventoryObservation(status)!;
  expect(observed.capabilities).toContain("cameras");
  expect(observed.instances.cameras).toEqual(["base"]);
  expect(observed.source.profile.name).toBe("base101");
});
test("unavailable inventory retains qualified last observation, never a fabricated empty inventory", () => {
  const status = fixtures.find(
    (item) => item.name === "agent_unavailable_with_cache",
  )!.value;
  expect(status.inventory?.state).toBe("unavailable");
  expect(inventoryObservation(status)?.capabilities).toContain("imu");
  expect(inventoryObservation({ ...status, inventory: null })).toBeUndefined();
});
test("Daemon update states use current presence and comparable versions", () => {
  const status = fixtures.find((item) => item.name === "online_status")!.value;
  expect(
    daemonUpdate({
      ...status,
      daemon_version: "1.0.0",
      recommended_daemon_version: "1.0.0",
    }),
  ).toBe("current");
  expect(
    daemonUpdate({
      ...status,
      daemon_version: "0.1.0",
      recommended_daemon_version: "1.0.0",
    }),
  ).toBe("update available");
  expect(
    daemonUpdate({
      ...status,
      daemon_version: "2.0.0",
      recommended_daemon_version: "1.0.0",
    }),
  ).toBe("incompatible");
  expect(
    daemonUpdate({
      ...status,
      connectivity: "offline",
      daemon_version: "0.1.0",
      recommended_daemon_version: "1.0.0",
    }),
  ).toBe("unknown");
});
test("Agent update status uses only a fresh inventory and never gates App execution", () => {
  const fixture = fixtures.find((item) => item.name === "online_status");
  if (!fixture) throw new Error("Expected an online status fixture");
  const status = fixture.value;
  const inventory = status.inventory;
  const current = inventory?.current;
  if (!inventory || !current)
    throw new Error("Expected a current inventory fixture");
  const withVersion = (installed: string): RobotAppStatus => ({
    ...status,
    recommended_agent_version: "1.0.0",
    inventory: {
      ...inventory,
      current: {
        ...current,
        source: {
          ...current.source,
          agent_version: installed,
        },
      },
    },
  });
  expect(agentUpdate(withVersion("1.0.0"))).toBe("current");
  expect(agentUpdate(withVersion("0.9.0"))).toBe("update available");
  expect(agentUpdate(withVersion("2.0.0"))).toBe("incompatible");
  expect(
    agentUpdate({ ...withVersion("1.0.0"), recommended_agent_version: null }),
  ).toBe("unknown");
  expect(agentUpdate(status)).toBe("unknown");
  expect(
    agentUpdate({ ...withVersion("0.9.0"), connectivity: "offline" }),
  ).toBe("unknown");
  expect(
    agentUpdate({
      ...withVersion("0.9.0"),
      inventory: { ...inventory, state: "unavailable" },
    }),
  ).toBe("unknown");
  expect(
    agentUpdate({
      ...withVersion("0.9.0"),
      inventory: {
        state: "unavailable",
        attempted_at: "2026-11-28T00:00:00Z",
        reason: "unsupported_agent_protocol",
      },
    }),
  ).toBe("incompatible");
});
