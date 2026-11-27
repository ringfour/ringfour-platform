import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { expect, test } from "vitest";
import { inventoryObservation, type RobotAppStatus } from "./robot-status";

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
