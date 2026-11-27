import { useEffect, useState } from "react";
import { PageHeading } from "@/components/PageHeading";
import { RunTimeline } from "@/features/logbook/RunTimeline";
import {
  loadOverview,
  type OverviewData,
} from "@/features/overview/overview-data";
import { useApiToken } from "@/hooks/useApiToken";
import type {} from "@/lib/apps";
import { timelineListPath, useLocation } from "@/lib/location";
import "@/styles/app-workflows.css";
import "@/features/logbook/logbook.css";

export function AppTimelinePage({
  organizationId,
}: {
  organizationId: string;
}) {
  const token = useApiToken();
  const location = useLocation();
  const [data, setData] = useState<OverviewData | null>(null);
  const [error, setError] = useState("");
  useEffect(() => {
    let live = true;
    setData(null);
    loadOverview(organizationId, token, { attribution: true })
      .then((value) => {
        if (live) {
          setData(value);
          setError("");
        }
      })
      .catch((cause) => {
        if (live)
          setError(
            cause instanceof Error
              ? cause.message
              : "App timeline is unavailable.",
          );
      });
    return () => {
      live = false;
    };
  }, [organizationId, token]);
  return (
    <main className="logbook2-page">
      <PageHeading
        title="Timeline"
        description="See when runs overlapped and in what order their events happened. Times use your local timezone."
        actions={
          <nav className="app-actions" aria-label="Logbook views">
            <a
              className="button-secondary"
              href={timelineListPath(organizationId, location.searchParams)}
            >
              List
            </a>
            <a
              className="button-secondary"
              aria-current="page"
              href={location.pathname + location.search}
            >
              Timeline
            </a>
          </nav>
        }
      >
        <div id="console-timeline-toolbar" />
      </PageHeading>
      {error && (
        <p role="alert" className="app-feedback app-feedback-error">
          {error}
        </p>
      )}
      {data ? (
        <RunTimeline
          robots={data.robots.filter(
            (robot) =>
              !location.searchParams.get("robot") ||
              robot.id === location.searchParams.get("robot"),
          )}
          robotFilter={location.searchParams.get("robot") ?? undefined}
          runs={data.runs.filter(
            (run) =>
              (!location.searchParams.get("app_id") ||
                run.appId === location.searchParams.get("app_id")) &&
              (!location.searchParams.get("state") ||
                run.state === location.searchParams.get("state")),
          )}
          workspace={organizationId}
          token={token}
        />
      ) : (
        <section className="app-panel">
          <p>{error || "Loading App timeline…"}</p>
        </section>
      )}
    </main>
  );
}
