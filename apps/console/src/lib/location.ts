import { type MouseEvent, useSyncExternalStore } from "react";

const listeners = new Set<() => void>();
let acceptedHref = window.location.href;
let acceptedIndex = 0;
let acceptedState = window.history.state;
let restoring = false;
function publish() {
  acceptedHref = window.location.href;
  acceptedIndex = window.history.state?.studioIndex ?? 0;
  acceptedState = window.history.state;
  for (const notify of listeners) notify();
}
window.addEventListener("ringfour:location", publish);
window.addEventListener("popstate", () => {
  if (restoring) {
    restoring = false;
    return;
  }
  const targetIndex = window.history.state?.studioIndex ?? 0;
  const navigation = new CustomEvent("ringfour:navigation", {
    cancelable: true,
    detail: { href: window.location.href, source: "history" },
  });
  if (listeners.size && !window.dispatchEvent(navigation)) {
    const delta = acceptedIndex - targetIndex;
    if (delta) {
      restoring = true;
      window.history.go(delta);
    } else window.history.replaceState(acceptedState, "", acceptedHref);
    return;
  }
  window.dispatchEvent(new Event("ringfour:location"));
  window.dispatchEvent(new Event("ringfour:navigated"));
});
function subscribe(notify: () => void) {
  if (!listeners.size) {
    if (window.history.state?.studioIndex === undefined)
      window.history.replaceState(
        { ...window.history.state, studioIndex: 0 },
        "",
      );
    publish();
  }
  listeners.add(notify);
  return () => {
    listeners.delete(notify);
  };
}
export function useLocation() {
  return new URL(
    useSyncExternalStore(subscribe, () =>
      listeners.size ? acceptedHref : window.location.href,
    ),
  );
}

export function visit(path: string): boolean {
  const next = new URL(path, window.location.origin);
  if (next.href === window.location.href) return true;
  const navigation = new CustomEvent("ringfour:navigation", {
    cancelable: true,
    detail: { href: next.href },
  });
  if (!window.dispatchEvent(navigation)) return false;
  const previous = window.location.pathname;
  const returning =
    ["/runs", "/robots", "/apps", "/hub", "/timeline"].includes(previous) &&
    (next.pathname.startsWith(previous + "/") ||
      (previous === "/timeline" && next.pathname.startsWith("/runs/")));
  const fromList = returning
    ? previous + window.location.search
    : previous.split("/")[1] === next.pathname.split("/")[1]
      ? window.history.state?.fromList
      : undefined;
  const isUtility = (pathname: string) =>
    ["/settings/profile", "/settings/workspace", "/settings/health"].includes(
      pathname,
    );
  const previousRoute = resolveStudioRoute(new URL(window.location.href));
  const robotOrigin =
    previousRoute.view === "robots" &&
    previousRoute.id &&
    previousRoute.id !== "enroll";
  const fromRobot = isUtility(next.pathname)
    ? robotOrigin
      ? previous + window.location.search
      : isUtility(previous)
        ? window.history.state?.fromRobot
        : undefined
    : undefined;
  window.history.replaceState(
    { ...window.history.state, scrollY: window.scrollY },
    "",
  );
  window.history.pushState(
    {
      fromList,
      fromRobot,
      storeFocus:
        next.pathname === "/hub" && next.pathname + next.search === fromList
          ? resolveStudioRoute(new URL(window.location.href)).id
          : undefined,
      studioIndex: (window.history.state?.studioIndex ?? 0) + 1,
      scrollY:
        next.pathname + next.search === window.history.state?.fromList
          ? (window.history.state?.listScroll ?? 0)
          : 0,
      listScroll: returning ? window.scrollY : window.history.state?.listScroll,
    },
    "",
    next.pathname + next.search + next.hash,
  );
  window.dispatchEvent(new Event("ringfour:location"));
  window.dispatchEvent(new Event("ringfour:navigated"));
  return true;
}

export function setQuery(name: string, value: string) {
  const url = new URL(window.location.href);
  if (value) url.searchParams.set(name, value);
  else url.searchParams.delete(name);
  visit(url.pathname + url.search);
}

export function resourcePath(
  collection: string,
  id: string,
  workspace: string,
) {
  const path =
    "/" +
    (collection === "logbook" ? "runs" : collection) +
    (id ? "/" + encodeURIComponent(id) : "");
  return workspace ? path + "?" + new URLSearchParams({ workspace }) : path;
}

export type StudioView =
  | "overview"
  | "apps"
  | "store"
  | "robots"
  | "logbook"
  | "timeline"
  | "identity"
  | "health"
  | "profile";
export type StudioRoute = {
  view: StudioView | "unavailable";
  id: string;
  section?: string;
  normalized?: string;
};
const destinations: Record<StudioView, string> = {
  overview: "overview",
  apps: "apps",
  store: "hub",
  robots: "robots",
  logbook: "runs",
  timeline: "timeline",
  identity: "settings/workspace",
  health: "settings/health",
  profile: "settings/profile",
};
export function studioPath(
  view: StudioView,
  workspace: string,
  id = "",
  query: Record<string, string | undefined> = {},
) {
  const url = new URL(
    resourcePath(destinations[view], id, workspace),
    window.location.origin,
  );
  for (const [key, value] of Object.entries(query))
    if (value) url.searchParams.set(key, value);
  return url.pathname + url.search;
}
export function appPath(
  app: string,
  workspace: string,
  installation?: string,
  section = "overview",
) {
  const url = new URL(
    studioPath("apps", workspace, app, { installation }),
    window.location.origin,
  );
  url.pathname += "/" + section;
  return url.pathname + url.search;
}
export function resolveStudioRoute(url: URL): StudioRoute {
  const path = url.pathname.replace(/\/$/, "");
  if (!path || path === "/logbook" || path.startsWith("/logbook/")) {
    const normalized =
      (path ? path.replace(/^\/logbook/, "/runs") : "/overview") +
      url.search +
      url.hash;
    return {
      ...resolveStudioRoute(new URL(normalized, url.origin)),
      normalized,
    };
  }
  if (path === "/app-store" || path.startsWith("/app-store/")) {
    const normalized =
      path.replace(/^\/app-store/, "/hub") + url.search + url.hash;
    return {
      ...resolveStudioRoute(new URL(normalized, url.origin)),
      normalized,
    };
  }
  const parts = path.slice(1).split("/");
  const id = parts[1] ?? "";
  if (
    parts[0] === "apps" &&
    parts.length <= 3 &&
    (!parts[2] ||
      ["overview", "settings", "files", "activity"].includes(parts[2]))
  )
    return { view: "apps", id, section: parts[2] ?? "overview" };
  if (
    parts[0] === "robots" &&
    (parts.length <= 2 ||
      (parts.length === 3 && id !== "enroll" && parts[2] === "credentials"))
  )
    return {
      view: "robots",
      id,
      section: id === "enroll" ? "enroll" : parts[2],
    };
  for (const view of Object.keys(destinations) as StudioView[])
    if (
      !["apps", "robots"].includes(view) &&
      (path === "/" + destinations[view] ||
        (["store", "logbook"].includes(view) &&
          parts.length === 2 &&
          parts[0] === destinations[view]))
    )
      return { view, id: ["store", "logbook"].includes(view) ? id : "" };
  return { view: "unavailable", id: "" };
}

/** Preserve native links, modifier clicks, downloads, fragments and external destinations. */
export function followStudioLink(
  event: MouseEvent<HTMLElement> | globalThis.MouseEvent,
) {
  if (
    event.defaultPrevented ||
    event.button !== 0 ||
    event.metaKey ||
    event.ctrlKey ||
    event.shiftKey ||
    event.altKey
  )
    return;
  const anchor = (event.target as Element).closest<HTMLAnchorElement>(
    "a[href]",
  );
  if (
    !anchor ||
    anchor.hasAttribute("download") ||
    anchor.target ||
    anchor.getAttribute("href")?.startsWith("#")
  )
    return;
  const url = new URL(anchor.href);
  if (
    url.origin !== window.location.origin ||
    resolveStudioRoute(url).view === "unavailable"
  )
    return;
  event.preventDefault();
  visit(url.pathname + url.search + url.hash);
}

export function robotCredentialsPath(robot: string, workspace: string) {
  const url = new URL(
    studioPath("robots", workspace, robot),
    window.location.origin,
  );
  url.pathname += "/credentials";
  return url.pathname + url.search;
}

/** State within the current page, such as an accepted operation, is not navigation. */
export function replaceQuery(values: Record<string, string | undefined>) {
  const url = new URL(window.location.href);
  for (const [key, value] of Object.entries(values))
    value ? url.searchParams.set(key, value) : url.searchParams.delete(key);
  window.history.replaceState(
    window.history.state,
    "",
    url.pathname + url.search + url.hash,
  );
  window.dispatchEvent(new Event("ringfour:location"));
}

export function parentPath(view: StudioView, workspace: string) {
  const from = window.history.state?.fromList;
  if (typeof from === "string") {
    const url = new URL(from, window.location.origin);
    const route = resolveStudioRoute(url);
    if (
      url.origin === window.location.origin &&
      route.view === view &&
      !route.id &&
      url.searchParams.get("workspace") === workspace
    )
      return url.pathname + url.search;
  }
  return studioPath(view, workspace);
}

export function timelinePath(
  workspace: string,
  filters: {
    robot_id?: string;
    app_id?: string;
    state?: string;
    from?: string;
    until?: string;
  },
) {
  const end = filters.until ? Date.parse(filters.until) : Date.now();
  const hours = filters.from ? (end - Date.parse(filters.from)) / 3600000 : 0;
  return studioPath("timeline", workspace, "", {
    robot: filters.robot_id,
    app_id: filters.app_id,
    state: filters.state,
    until: Number.isFinite(end) ? String(end) : undefined,
    hours: hours > 0 ? String(hours) : undefined,
  });
}
export function timelineListPath(workspace: string, params: URLSearchParams) {
  const end = Number(params.get("until"));
  const span = Number(params.get("hours")) || 24;
  const local = (time: number) => {
    const date = new Date(time);
    return new Date(time - date.getTimezoneOffset() * 60000)
      .toISOString()
      .slice(0, 16);
  };
  return studioPath("logbook", workspace, "", {
    robot_id: params.get("robot") ?? undefined,
    app_id: params.get("app_id") ?? undefined,
    state: params.get("state") ?? undefined,
    until: end > 0 ? local(end) : undefined,
    from: end > 0 ? local(end - span * 3600000) : undefined,
  });
}
