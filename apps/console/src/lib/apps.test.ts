import { describe, expect, it, vi } from "vitest";
import { readLiveRuns } from "./apps";

describe("readLiveRuns", () => {
  it("adds the page limit to an existing recent-first query", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockImplementation(
      async () =>
        new Response(JSON.stringify({ items: [] }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        }),
    );

    await readLiveRuns(async () => "test-token", "/v1/organizations/org/app-runs");

    const urls = fetchMock.mock.calls.map(([input]) => String(input));
    expect(urls).toContain(
      "/api/v1/organizations/org/app-runs?recent_first=true&limit=50",
    );
    expect(urls).toContain(
      "/api/v1/organizations/org/app-runs?active_only=true",
    );
    fetchMock.mockRestore();
  });
});
