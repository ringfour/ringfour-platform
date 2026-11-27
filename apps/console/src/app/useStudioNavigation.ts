import { useEffect } from "react";
import {
  followStudioLink,
  resolveStudioRoute,
  useLocation,
} from "@/lib/location";
export function useStudioNavigation() {
  const location = useLocation();
  const route = resolveStudioRoute(location);
  useEffect(() => {
    if (!route.normalized) return;
    window.history.replaceState(window.history.state, "", route.normalized);
    window.dispatchEvent(new PopStateEvent("popstate"));
  }, [route.normalized]);
  useEffect(() => {
    const restore = () =>
      requestAnimationFrame(() =>
        requestAnimationFrame(() =>
          window.scrollTo(0, window.history.state?.scrollY ?? 0),
        ),
      );
    const priorRestoration = window.history.scrollRestoration;
    window.history.scrollRestoration = "manual";
    window.addEventListener("ringfour:navigated", restore);
    return () => {
      window.history.scrollRestoration = priorRestoration;
      window.removeEventListener("ringfour:navigated", restore);
    };
  }, []);
  useEffect(() => {
    document.addEventListener("click", followStudioLink);
    return () => document.removeEventListener("click", followStudioLink);
  }, []);
  return route;
}
