import { useEffect, useRef, useState } from "react";
import { eventTitle } from "@/lib/app-history";
import {
  type AppLogbookEvent,
  type AppRun,
  readFirstPage,
  type Token,
  withQuery,
} from "@/lib/apps";
import { resourcePath, setQuery, useLocation } from "@/lib/location";
import { RunHistoryRow } from "@/views/RunHistoryRow";
import { runStateLabel } from "@/views/RunStatus";
import { StudioSection } from "@/views/StudioPage";

function descriptionFor(event: AppLogbookEvent) {
  const reason = event.payload.reason;
  if (typeof reason === "string" && reason.trim()) return reason;
  const state = event.payload.state;
  if (typeof state === "string" && state.trim()) return `State: ${state}`;
  return "Recorded in the workspace App Logbook.";
}

export function AppActivity({
  appId,
  organizationId,
  robots,
  runs,
  installationId,
  token,
}: {
  appId: string;
  organizationId: string;
  robots: { id: string; name: string }[];
  runs: AppRun[];
  installationId: string;
  token: Token;
}) {
  const [events, setEvents] = useState<AppLogbookEvent[]>([]);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [refresh, setRefresh] = useState(0);
  const location = useLocation();
  const runFilter = location.searchParams.get("run") ?? "";
  const loadedScope = useRef("");
  useEffect(() => {
    void refresh;
    let live = true;
    const scope = `${organizationId}:${appId}:${installationId}`;
    if (loadedScope.current !== scope) {
      loadedScope.current = scope;
      setEvents([]);
      setLoading(true);
    }
    setError("");
    void readFirstPage<AppLogbookEvent>(
      token,
      withQuery(`/v1/organizations/${organizationId}/app-logbook`, {
        app_id: appId,
        installation_id: installationId,
        recent_first: "true",
      }),
      100,
    )
      .then((rows) => {
        if (live) setEvents(rows);
      })
      .catch((cause) => {
        if (live)
          setError(
            cause instanceof Error
              ? cause.message
              : "App activity is unavailable.",
          );
      })
      .finally(() => {
        if (live) setLoading(false);
      });
    return () => {
      live = false;
    };
  }, [appId, installationId, organizationId, refresh, token]);
  const visibleEvents = events.filter(
    (event) =>
      event.installation_id === installationId &&
      (!runFilter || event.run_id === runFilter),
  );

  const scopedRuns = runs.filter(
    (run) =>
      run.installation_id === installationId &&
      (!runFilter || run.id === runFilter),
  );
  const groupIds = [
    ...new Set([
      ...scopedRuns.map((run) => run.id),
      ...visibleEvents.map((event) => event.run_id ?? "installation"),
    ]),
  ];
  groupIds.sort((left, right) => {
    const stamp = (id: string) =>
      runs.find((run) => run.id === id)?.requested_at ??
      visibleEvents.find((event) => (event.run_id ?? "installation") === id)
        ?.occurred_at ??
      "";
    return stamp(right).localeCompare(stamp(left));
  });
  return (
    <StudioSection
      title="Installation activity"
      description="Runs and events for this installation."
      titleId="app-activity-title"
      className="app-activity-panel"
      actions={
        <button
          className="button-secondary"
          type="button"
          onClick={() => setRefresh((value) => value + 1)}
        >
          Refresh
        </button>
      }
    >
      <div className="studio-filter-bar">
        {runs.length > 0 && (
          <label className="app-field" htmlFor="app-activity-run">
            Run
            <select
              id="app-activity-run"
              value={runFilter}
              onChange={(event) => setQuery("run", event.target.value)}
            >
              <option value="">All App runs</option>
              {runs
                .filter((run) => run.installation_id === installationId)
                .map((run) => (
                  <option key={run.id} value={run.id}>
                    {robots.find((robot) => robot.id === run.robot_id)?.name ??
                      "Robot"}{" "}
                    · {runStateLabel(run.state)} ·{" "}
                    {new Date(run.requested_at).toLocaleString()}
                  </option>
                ))}
            </select>
          </label>
        )}
        {events.length === 100 && (
          <p className="studio-result-count">
            Latest 100 events loaded · Counts may be partial
          </p>
        )}
      </div>
      {error && events.length > 0 && (
        <p role="alert" className="app-feedback app-feedback-error">
          {error} Showing the last loaded activity snapshot.
        </p>
      )}
      {error && events.length === 0 && !loading && (
        <div className="app-library-empty" role="alert">
          <h3>App activity could not be loaded</h3>
          <p>{error}</p>
          <button
            className="button-secondary"
            type="button"
            onClick={() => setRefresh((value) => value + 1)}
          >
            Try again
          </button>
        </div>
      )}
      {loading ? (
        <p role="status">Loading App activity…</p>
      ) : error && events.length === 0 ? null : groupIds.length ? (
        <div className="studio-history-list">
          {groupIds.map((runId) => {
            const records = visibleEvents.filter(
              (event) => (event.run_id ?? "installation") === runId,
            );
            const run = scopedRuns.find((item) => item.id === runId);
            const robot = robots.find(
              (item) => item.id === (run?.robot_id ?? records[0]?.robot_id),
            );
            return (
              <RunHistoryRow
                key={runId}
                title={
                  run
                    ? "App run"
                    : runId === "installation"
                      ? "Installation activity"
                      : "Recorded run"
                }
                detail={robot?.name ?? "Robot"}
                state={run?.state}
                time={run?.requested_at ?? records[0]?.occurred_at ?? ""}
                href={
                  runId !== "installation"
                    ? resourcePath("runs", runId, organizationId)
                    : undefined
                }
                eventCount={records.length}
                partial={events.length === 100}
              >
                <ol>
                  {records.map((event) => (
                    <li key={event.event_id}>
                      <strong>{eventTitle(event)}</strong>
                      <p>{descriptionFor(event)}</p>
                      <small>
                        {new Date(event.occurred_at).toLocaleString()}
                      </small>
                      <details className="app-technical-details">
                        <summary>Technical details</summary>
                        <code>{event.event_id}</code>
                        {event.operation_id && (
                          <p>Operation {event.operation_id}</p>
                        )}
                      </details>
                    </li>
                  ))}
                </ol>
              </RunHistoryRow>
            );
          })}
        </div>
      ) : (
        <div className="app-library-empty">
          <h3>
            {events.length
              ? "No activity matches these filters"
              : "No App activity yet"}
          </h3>
          <p>
            {events.length
              ? "Choose another run or clear the selected robot to see other records."
              : "App events recorded in Logbook will appear here with their robot and run context."}
          </p>
        </div>
      )}
    </StudioSection>
  );
}
