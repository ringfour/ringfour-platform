import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, expect, test, vi } from "vitest";

import { HealthPage } from "./HealthPage";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function readyResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}

test("renders ok status and checks for healthy services", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input) => {
      const url = String(input);
      if (url.startsWith("/api/")) {
        return readyResponse({
          status: "ok",
          version: "0.1.0",
          checks: { postgresql: "ok" },
        });
      }
      return readyResponse({
        status: "ok",
        version: "0.1.0",
        checks: { sqlite: "ok", runner: "ok" },
      });
    }),
  );

  render(<HealthPage />);

  expect(await screen.findByTestId("api-status")).toHaveProperty(
    "textContent",
    "ok",
  );
  expect(await screen.findByTestId("daemon-status")).toHaveProperty(
    "textContent",
    "ok",
  );
  expect(screen.getByText("postgresql=ok")).toBeTruthy();
  expect(screen.getByText("sqlite=ok, runner=ok")).toBeTruthy();
});

test("renders fail when a service reports failed readiness", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input) => {
      const url = String(input);
      if (url.startsWith("/api/")) {
        return readyResponse(
          { status: "fail", version: "0.1.0", checks: { postgresql: "fail" } },
          503,
        );
      }
      return readyResponse({
        status: "ok",
        version: "0.1.0",
        checks: { sqlite: "ok", runner: "ok" },
      });
    }),
  );

  render(<HealthPage />);

  expect(await screen.findByTestId("api-status")).toHaveProperty(
    "textContent",
    "fail",
  );
  expect(await screen.findByTestId("daemon-status")).toHaveProperty(
    "textContent",
    "ok",
  );
});

test("handles unreachable services", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn(async () => {
      throw new TypeError("fetch failed");
    }),
  );

  render(<HealthPage />);

  expect(await screen.findByTestId("api-status")).toHaveProperty(
    "textContent",
    "unreachable",
  );
  expect(await screen.findByTestId("daemon-status")).toHaveProperty(
    "textContent",
    "unreachable",
  );
});
