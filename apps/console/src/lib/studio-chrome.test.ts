import { afterEach, expect, test } from "vitest";
import { chromeBackLink } from "./studio-chrome";

const back = (path: string) =>
  chromeBackLink(new URL(path, window.location.origin), "org");
afterEach(() => window.history.replaceState(null, "", "/"));

test("App back navigation preserves the collection's workspace, robot and search", () => {
  const href = "/apps?workspace=org&robot=robot-b&q=camera";
  window.history.replaceState({ fromList: href }, "", "/");
  expect(
    back("/apps/camera/settings?workspace=org&installation=install-b"),
  ).toEqual({ href, label: "Back to Apps" });
});

test("direct run links retain history filters but discard section and operation parameters", () => {
  expect(
    back(
      "/runs/run?workspace=org&group=Finished&robot_id=b&state=failed&tab=Output&operation=stop",
    ),
  ).toEqual({
    href: "/runs?workspace=org&group=Finished&robot_id=b&state=failed",
    label: "Back to runs",
  });
  window.history.replaceState(
    { fromList: "/runs?workspace=org&group=All&actor=user" },
    "",
    "/",
  );
  expect(back("/runs/run?workspace=org&tab=Output")?.href).toBe(
    "/runs?workspace=org&group=All&actor=user",
  );
});

test("run records opened from Timeline return to its original time and robot filters", () => {
  const href = "/timeline?workspace=org&robot=b&hours=2&until=1795604400000";
  window.history.replaceState({ fromList: href }, "", "/");
  expect(back("/runs/run?workspace=org")).toEqual({
    href,
    label: "Back to timeline",
  });
  const target = back(href);
  if (!target) throw new Error("Timeline has no parent");
  const list = new URL(target.href, window.location.origin);
  expect(list.pathname).toBe("/runs");
  expect(list.searchParams.get("robot_id")).toBe("b");
  expect(list.searchParams.get("from")).toBeTruthy();
  expect(list.searchParams.get("until")).toBeTruthy();
});

test("credentials return to the same robot, while enrollment returns to the collection", () => {
  expect(back("/robots/robot-b/credentials?workspace=org")).toEqual({
    href: "/robots/robot-b?workspace=org",
    label: "Back to robot",
  });
  expect(back("/robots/enroll?workspace=org")).toEqual({
    href: "/robots?workspace=org",
    label: "Back to robots",
  });
});

test.each([
  "https://outside.invalid/apps?workspace=org",
  "/apps?workspace=foreign",
  "http://[",
])("stale or invalid parent %s cannot change the workspace", (fromList) => {
  window.history.replaceState({ fromList }, "", "/");
  expect(back("/apps/camera?workspace=org")).toEqual({
    href: "/apps?workspace=org",
    label: "Back to Apps",
  });
  expect(back("/hub/camera?workspace=org&robot=b&q=photo")).toEqual({
    href: "/hub?workspace=org&robot=b&q=photo",
    label: "Back to Hub",
  });
});

test("collection pages have no invented back destination", () => {
  for (const path of [
    "/overview",
    "/apps",
    "/hub",
    "/robots",
    "/runs",
    "/settings/workspace",
    "/settings/profile",
    "/settings/health",
  ])
    expect(back(`${path}?workspace=org`)).toBeUndefined();
});
