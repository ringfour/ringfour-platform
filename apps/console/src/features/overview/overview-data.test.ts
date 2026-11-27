import { afterEach, expect, test, vi } from "vitest";
import { loadOverview } from "@/features/overview/overview-data";

afterEach(() => vi.unstubAllGlobals());
test("Timeline still requests attribution explicitly, without invisible artifact counts", async () => {
  const paths: string[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input), "http://localhost").pathname;
      paths.push(path);
      return Response.json(
        path.endsWith("/me")
          ? { user: { id: "user" } }
          : { items: [], next_cursor: null },
      );
    }),
  );
  await loadOverview("org", async () => "token", { attribution: true });
  expect(paths).toContain("/api/v1/me");
  expect(paths).toContain("/api/v1/organizations/org/members");
  expect(paths.some((path) => path.endsWith("/artifacts"))).toBe(false);
});
