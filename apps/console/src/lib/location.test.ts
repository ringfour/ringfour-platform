import { afterEach, expect, test } from "vitest";
import { appPath, resolveStudioRoute, studioPath, visit } from "./location";

const resolve = (path: string) =>
  resolveStudioRoute(new URL(path, "http://localhost"));
afterEach(() => window.history.replaceState(null, "", "/"));

test("aliases normalize with replacement targets and preserve filters and fragments", () => {
  expect(resolve("/logbook/run?workspace=w&robot=r#files")).toEqual({
    view: "logbook",
    id: "run",
    normalized: "/runs/run?workspace=w&robot=r#files",
  });
  expect(resolve("/").normalized).toBe("/overview");
  expect(resolve("/unrecognized").view).toBe("unavailable");
  expect(resolve("/apps/app/obsolete").view).toBe("unavailable");
  expect(resolve("/robots/enroll")).toEqual({
    view: "robots",
    id: "enroll",
    section: "enroll",
  });
  expect(resolve("/robots/robot/credentials")).toEqual({
    view: "robots",
    id: "robot",
    section: "credentials",
  });
});

test("App section links carry exact robot installation and workspace", () => {
  expect(appPath("camera", "workshop", "robot-b-install", "settings")).toBe(
    "/apps/camera/settings?workspace=workshop&installation=robot-b-install",
  );
  expect(
    resolve(appPath("camera", "workshop", "robot-b-install", "settings")),
  ).toEqual({ view: "apps", id: "camera", section: "settings" });
  expect(
    studioPath("store", "workshop", "camera", {
      robot: "robot-b",
      release: "release",
    }),
  ).toBe("/hub/camera?workspace=workshop&robot=robot-b&release=release");
});

test("dirty navigation cancellation leaves URL and history unchanged", () => {
  window.history.replaceState(
    { marker: "preserve" },
    "",
    "/apps/app/settings?workspace=w",
  );
  const reject = (event: Event) => event.preventDefault();
  window.addEventListener("ringfour:navigation", reject);
  try {
    expect(visit("/overview?workspace=w")).toBe(false);
  } finally {
    window.removeEventListener("ringfour:navigation", reject);
  }
  expect(window.location.pathname).toBe("/apps/app/settings");
  expect(window.history.state.marker).toBe("preserve");
});

test("List and Timeline keep the same robot, App, state and time window", async () => {
  const { timelinePath, timelineListPath } = await import("./location");
  const filters = {
    robot_id: "robot-b",
    app_id: "camera",
    state: "failed",
    from: "2026-11-25T09:00",
    until: "2026-11-25T11:00",
  };
  const timeline = new URL(timelinePath("org", filters), "http://localhost");
  expect(timeline.searchParams.get("hours")).toBe("2");
  const list = new URL(
    timelineListPath("org", timeline.searchParams),
    "http://localhost",
  );
  for (const [key, value] of Object.entries(filters))
    expect(list.searchParams.get(key)).toBe(value);
});

test("legacy Store bookmarks redirect to the Hub with their filters", () => {
  const input = new URL(
    "https://example.test/app-store/camera?workspace=org&robot=one#details",
  );
  expect(resolveStudioRoute(input)).toEqual({
    view: "store",
    id: "camera",
    normalized: "/hub/camera?workspace=org&robot=one#details",
  });
});
