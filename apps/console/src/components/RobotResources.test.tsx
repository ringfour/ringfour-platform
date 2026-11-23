import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, expect, test } from "vitest";
import { RobotResources } from "./RobotResources";
afterEach(cleanup);
test("distinguishes an absent sample from low battery and last-known free storage", () => {
  const { rerender } = render(<RobotResources />);
  expect(screen.queryByRole("meter")).toBeNull();
  rerender(
    <RobotResources
      sample={{
        stale: true,
        observedAt: "2026-11-20T09:00:00Z",
        battery: { percent: 4, supply: "discharging" },
        storage: { freeBytes: 1024 ** 3, totalBytes: 32 * 1024 ** 3 },
      }}
    />,
  );
  expect(screen.getByText(/Last known resources/)).toBeTruthy();
  expect(
    screen
      .getByRole("meter", { name: "Battery remaining" })
      .getAttribute("value"),
  ).toBe("4");
  expect(screen.getByText("1 GiB free of 32 GiB")).toBeTruthy();
});
