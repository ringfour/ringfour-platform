import { PageHeading } from "@/components/PageHeading";
import { activeRun } from "@/lib/app-history";
import { replaceQuery, timelinePath } from "@/lib/location";
import { RunHistoryRow } from "@/views/RunHistoryRow";
import { runStateLabel } from "@/views/RunStatus";
import { StudioPage } from "@/views/StudioPage";
import { StudioTabs } from "@/views/StudioTabs";
import { groups } from "./logbook-model";
import type { LogbookModel } from "./useLogbookController";

function Filter({
  label,
  value,
  onChange,
  children,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  children: React.ReactNode;
}) {
  return (
    <label className="app-field">
      <span>{label}</span>
      <select value={value} onChange={(event) => onChange(event.target.value)}>
        {children}
      </select>
    </label>
  );
}

export function RunCollectionView({ model }: { model: LogbookModel }) {
  const {
    organizationId,
    robots,
    runs,
    apps,
    pendingError,
    error,
    loading,
    name,
    person,
    release,
    link,
    update,
    filtersActive,
    visible,
    group,
    appFilter,
    robotFilter,
    actorFilter,
    stateFilter,
    days,
    from,
    until,
    reload,
  } = model;
  return (
    <StudioPage
      className="logbook2-page"
      heading={
        <PageHeading
          title="Logbook"
          description="Search run history across this workspace."
          actions={
            <div className="app-actions">
              <a
                className="button-secondary"
                href={timelinePath(organizationId, {
                  app_id: appFilter,
                  robot_id: robotFilter,
                  state: stateFilter,
                  from,
                  until,
                })}
              >
                Timeline
              </a>
              <button
                className="button-secondary"
                type="button"
                onClick={reload}
              >
                Refresh
              </button>
            </div>
          }
        />
      }
    >
      <div className="studio-history-scope">
        <p className="studio-active-summary">
          <strong>Active now</strong> · {runs.filter(activeRun).length} active
          runs
        </p>
        <section
          className="studio-filter-bar studio-run-filters"
          aria-label="Run filters"
        >
          <Filter
            label="Started within"
            value={days}
            onChange={(value) => update("days", value)}
          >
            <option value="">Any time</option>
            <option value="1">Last day</option>
            <option value="7">Last week</option>
            <option value="30">Last 30 days</option>
          </Filter>
          <Filter
            label="App"
            value={appFilter}
            onChange={(value) => update("app_id", value)}
          >
            <option value="">All Apps</option>
            {[...new Set(runs.map((run) => run.snapshot.app_id))].map((id) => (
              <option key={id} value={id}>
                {apps[id]?.name ?? id}
              </option>
            ))}
          </Filter>
          <Filter
            label="Robot"
            value={robotFilter}
            onChange={(value) => update("robot_id", value)}
          >
            <option value="">All robots</option>
            {robots.map((robot) => (
              <option key={robot.id} value={robot.id}>
                {robot.name}
              </option>
            ))}
          </Filter>
          <Filter
            label="Initiated by"
            value={actorFilter}
            onChange={(value) => update("actor", value)}
          >
            <option value="">All people</option>
            {[...new Set(runs.map((run) => run.requested_by))].map((id) => (
              <option key={id} value={id}>
                {person(id) ?? "Unknown initiator"}
              </option>
            ))}
          </Filter>
          <Filter
            label="Status"
            value={stateFilter}
            onChange={(value) => update("state", value)}
          >
            <option value="">All statuses</option>
            {[
              "accepted",
              "running",
              "succeeded",
              "failed",
              "stopped",
              "interrupted",
            ].map((state) => (
              <option key={state} value={state}>
                {runStateLabel(state)}
              </option>
            ))}
          </Filter>

          <div className="studio-run-date-range">
            <label className="app-field">
              From
              <input
                type="datetime-local"
                value={from}
                onChange={(event) => update("from", event.target.value)}
              />
            </label>
            <label className="app-field">
              Until
              <input
                type="datetime-local"
                value={until}
                onChange={(event) => update("until", event.target.value)}
              />
            </label>
          </div>
          <button
            type="button"
            className="button-secondary"
            disabled={!filtersActive}
            onClick={() =>
              replaceQuery(
                Object.fromEntries(
                  [
                    "app_id",
                    "app",
                    "robot_id",
                    "robot",
                    "actor",
                    "state",
                    "days",
                    "from",
                    "until",
                  ].map((key) => [key, undefined]),
                ),
              )
            }
          >
            Clear all
          </button>
        </section>
        {pendingError && (
          <p role="status" className="logbook-notice">
            {pendingError}
          </p>
        )}
        <section className="studio-run-list" aria-label="Runs">
          <header>
            <h2>Runs</h2>
            <span>
              {loading
                ? "Loading"
                : `${visible.length} record${visible.length === 1 ? "" : "s"}`}
            </span>
          </header>
          <StudioTabs label="Run groups">
            {groups.map((label) => (
              <a
                key={label}
                href={link("", { group: label })}
                aria-current={group === label ? "page" : undefined}
              >
                {label}
              </a>
            ))}
          </StudioTabs>
          {error && (
            <p role="alert" className="logbook2-empty">
              {error}
              {runs.length > 0 && " · Showing the last complete snapshot."}
            </p>
          )}
          {loading ? (
            <p role="status" className="logbook2-empty">
              Loading runs…
            </p>
          ) : !visible.length ? (
            <p className="logbook2-empty">
              {runs.length
                ? "No runs match these filters."
                : "No App runs recorded yet."}
            </p>
          ) : (
            <div className="studio-history-list">
              {visible.map((run) => (
                <RunHistoryRow
                  key={run.id}
                  title={name(run)}
                  openOnRow
                  detail={
                    <>
                      {release(run)
                        ? `Release ${release(run)}`
                        : "Release unavailable"}{" "}
                      ·{" "}
                      {robots.find((robot) => robot.id === run.robot_id)
                        ?.name ?? "Robot unavailable"}
                      {person(run.requested_by) &&
                        ` · Started by ${person(run.requested_by)}`}
                    </>
                  }
                  state={run.state}
                  time={run.started_at ?? run.requested_at}
                  href={link(run.id)}
                />
              ))}
            </div>
          )}
        </section>
      </div>
    </StudioPage>
  );
}
