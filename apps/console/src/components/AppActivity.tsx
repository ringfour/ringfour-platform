import { useEffect, useRef, useState } from "react";
import {
  type AppLogbookEvent,
  type AppRun,
  readFirstPage,
  type Token,
  withQuery,
} from "@/lib/apps";
import { resourcePath } from "@/lib/location";

const knownEventTitles: Record<string, string> = {
  "app.settings.selected": "Settings selected for next start",
  "app.settings.application_requested": "Live settings requested",
  "app.settings.applied": "Settings applied",
  "app.settings.rejected": "Settings rejected",
  "app.settings.ended_before_confirmation":
    "Run ended before settings were confirmed",
};

function titleFor(event: AppLogbookEvent) {
  const message = event.payload.message;
  if (typeof message === "string" && message.trim()) return message;
  return (
    knownEventTitles[event.type] ??
    event.type
      .replace(/^app\./, "")
      .replaceAll(/[._]/g, " ")
      .replace(/^./, (letter) => letter.toUpperCase())
  );
}

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
  installationId?: string;
  token: Token;
}) {
  const [events, setEvents] = useState<AppLogbookEvent[]>([]);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [refresh, setRefresh] = useState(0);
  const [runFilter, setRunFilter] = useState("");
  const loadedScope = useRef("");
  useEffect(() => {
    void refresh;
    let live = true;
    const scope = `${organizationId}:${appId}`;
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
        recent_first: "true",
      }),
      100,
    )
      .then((rows) => {
        if (live)
          setEvents(rows);
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
  }, [appId, organizationId, refresh, token]);
  const visibleEvents = events.filter(
    (event) =>
      (!installationId || event.installation_id === installationId) &&
      (!runFilter || event.run_id === runFilter),
  );

  return (
    <section
      className="app-panel app-activity-panel"
      aria-labelledby="app-activity-title"
    >
      <header className="app-files-heading">
        <div>
          <p className="app-eyebrow">Activity</p>
          <h2 id="app-activity-title">App activity</h2>
          <p>These are the same durable records shown in Logbook.</p>
          {events.length === 100 && (
            <p className="app-files-scope">Showing the latest 100 events.</p>
          )}
        </div>
        <button
          className="button-secondary"
          type="button"
          onClick={() => setRefresh((value) => value + 1)}
        >
          Refresh
        </button>
      </header>
      <div className="app-files-filters">
        {runs.length > 0 && (
          <label className="app-field" htmlFor="app-activity-run">
            Run <span>Optional</span>
            <select
              id="app-activity-run"
              value={runFilter}
              onChange={(event) => setRunFilter(event.target.value)}
            >
              <option value="">All App runs</option>
              {runs
                .filter(
                  (run) =>
                    !installationId || run.installation_id === installationId,
                )
                .map((run) => (
                  <option key={run.id} value={run.id}>
                    {robots.find((robot) => robot.id === run.robot_id)?.name ??
                      "Robot"}{" "}
                    · {run.state} ·{" "}
                    {new Date(run.requested_at).toLocaleString()}
                  </option>
                ))}
            </select>
          </label>
        )}
        <p className="app-files-scope">
          {installationId
            ? "Filtered to the selected robot installation."
            : "Showing activity across this App’s installations."}
        </p>
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
      ) : error && events.length === 0 ? null : visibleEvents.length ? (
        <ol className="app-activity-list">
          {visibleEvents.map((event) => (
            <li key={event.event_id}>
              <div>
                <span className="app-activity-kind">App event</span>
                <strong>{titleFor(event)}</strong>
                <span className="app-activity-description">
                  {descriptionFor(event)}
                </span>
                <small className="app-activity-meta">
                  {robots.find((robot) => robot.id === event.robot_id)?.name ??
                    "Robot"}{" "}
                  · {new Date(event.occurred_at).toLocaleString()}
                </small>
                <details className="app-technical-details">
                  <summary>Event details</summary>
                  <code>Event {event.event_id}</code>
                  {event.operation_id && (
                    <>
                      <br />
                      <code>Operation {event.operation_id}</code>
                    </>
                  )}
                </details>
              </div>
              {event.run_id ? (
                <a href={resourcePath("runs", event.run_id, organizationId)}>
                  Open run
                </a>
              ) : (
                <span className="app-muted">No run linked</span>
              )}
            </li>
          ))}
        </ol>
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
    </section>
  );
}
