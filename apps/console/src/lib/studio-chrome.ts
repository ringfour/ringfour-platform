import { resolveStudioRoute, studioPath, timelineListPath } from "./location";

/** Shared back navigation retains collection context only within this workspace. */
export function chromeBackLink(
  url: URL,
  workspace: string,
): { href: string; label: string } | undefined {
  const route = resolveStudioRoute(url);
  let prior: URL | undefined;
  try {
    const from = window.history.state?.fromList;
    if (typeof from === "string") {
      const candidate = new URL(from, url.origin);
      if (
        candidate.origin === url.origin &&
        candidate.searchParams.get("workspace") === workspace
      )
        prior = candidate;
    }
  } catch {
    /* Invalid or stale navigation state falls back to the canonical parent. */
  }
  const previousRoute = prior && resolveStudioRoute(prior);
  const collection = (view: "apps" | "store" | "robots" | "logbook") =>
    prior && previousRoute?.view === view && !previousRoute.id
      ? prior.pathname + prior.search
      : undefined;
  if (["profile", "identity", "health"].includes(route.view)) {
    const source = window.history.state?.fromRobot;
    if (typeof source === "string" && source.startsWith("/robots/")) {
      const robotUrl = new URL(source, url.origin);
      const robot = resolveStudioRoute(robotUrl);
      if (
        robot.view === "robots" &&
        robot.id &&
        robotUrl.searchParams.get("workspace") === workspace
      )
        return {
          href: robotUrl.pathname + robotUrl.search,
          label: "Back to robot",
        };
    }
  }
  if (route.view === "apps" && route.id)
    return {
      href: collection("apps") ?? studioPath("apps", workspace),
      label: "Back to Apps",
    };
  if (route.view === "store" && route.id)
    return {
      href:
        collection("store") ??
        studioPath("store", workspace, "", {
          robot: url.searchParams.get("robot") ?? undefined,
          q: url.searchParams.get("q") ?? undefined,
        }),
      label: "Back to App Store",
    };
  if (route.view === "robots" && route.id)
    return {
      href:
        route.section === "credentials"
          ? studioPath("robots", workspace, route.id)
          : (collection("robots") ?? studioPath("robots", workspace)),
      label:
        route.section === "credentials" ? "Back to robot" : "Back to robots",
    };
  if (route.view === "logbook" && route.id) {
    if (prior && previousRoute?.view === "timeline")
      return { href: prior.pathname + prior.search, label: "Back to timeline" };
    const filters: Record<string, string> = {};
    for (const key of [
      "group",
      "app_id",
      "app",
      "robot_id",
      "robot",
      "actor",
      "state",
      "days",
      "from",
      "until",
    ]) {
      const value = url.searchParams.get(key);
      if (value) filters[key] = value;
    }
    return {
      href:
        collection("logbook") ?? studioPath("logbook", workspace, "", filters),
      label: "Back to runs",
    };
  }
  if (route.view === "timeline")
    return {
      href: timelineListPath(workspace, url.searchParams),
      label: "Back to runs",
    };
  if (route.view === "unavailable")
    return { href: studioPath("overview", workspace), label: "Back to Home" };
  return undefined;
}
