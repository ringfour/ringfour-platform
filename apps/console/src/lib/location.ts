import { useSyncExternalStore } from "react";

function subscribe(notify: () => void) {
  window.addEventListener("popstate", notify);
  return () => window.removeEventListener("popstate", notify);
}

export function useLocation() {
  return new URL(useSyncExternalStore(subscribe, () => window.location.href));
}

export function visit(path: string): boolean {
  const next = new URL(path, window.location.origin);
  const navigation = new CustomEvent("ringfour:navigation", {
    cancelable: true,
    detail: { href: next.href },
  });
  if (!window.dispatchEvent(navigation)) return false;
  const previous = window.location.pathname;
  const returning =
    ["/runs", "/robots", "/apps"].includes(previous) &&
    next.pathname.startsWith(previous + "/");
  const fromList = returning
    ? previous + window.location.search
    : previous === next.pathname
      ? window.history.state?.fromList
      : undefined;
  window.history.pushState(
    {
      fromList,
      listScroll: returning ? window.scrollY : window.history.state?.listScroll,
    },
    "",
    path,
  );
  window.dispatchEvent(new PopStateEvent("popstate"));
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
  const path = "/" + collection + (id ? "/" + encodeURIComponent(id) : "");
  return workspace ? path + "?" + new URLSearchParams({ workspace }) : path;
}
