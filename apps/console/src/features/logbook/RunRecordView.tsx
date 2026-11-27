import { PageHeading } from "@/components/PageHeading";
import { StudioTime } from "@/components/StudioTime";
import { AppFiles } from "@/features/files/AppFiles";
import { Decisions } from "@/features/interactions/Decisions";
import { eventTitle } from "@/lib/app-history";
import type { AppRun } from "@/lib/apps";
import { appPath, studioPath } from "@/lib/location";
import { RunStatus } from "@/views/RunStatus";
import { StudioTabs } from "@/views/StudioTabs";
import { AppLogbookEvents } from "./AppLogbookEvents";
import { AppRunOutput } from "./AppRunOutput";
import { duration } from "./logbook-model";
import { RunTimeline } from "./RunTimeline";
import type { LogbookModel } from "./useLogbookController";
export function RunRecordView({
  model,
  current,
  workspaceRole,
}: {
  model: LogbookModel;
  current: AppRun;
  workspaceRole?: string;
}) {
  const {
    robots,
    name,
    release,
    organizationId,
    running,
    person,
    refresh,
    error,
    events,
    eventsError,
    eventsLoading,
    tab,
    link,
    token,
    reload,
  } = model;

  const robot = robots.find((item) => item.id === current.robot_id) ?? {
    id: current.robot_id,
    name: "Robot unavailable",
  };
  const clip = {
    id: current.id,
    robot,
    app: name(current),
    version: release(current),
    state: current.state,
    created_at: current.started_at ?? current.requested_at,
    updated_at:
      current.ended_at ??
      (running ? (current.observed_at ?? undefined) : undefined),
    person: person(current.requested_by),
  };
  const appHref = appPath(
    current.snapshot.app_id,
    organizationId,
    current.installation_id,
  );
  return (
    <main className="logbook2-page logbook2-detail">
      <PageHeading
        className="studio-run-header"
        title={<a href={appHref}>{name(current)}</a>}
        metadata={<span className="logbook2-kicker">App run record</span>}
        description={<RunStatus state={current.state} />}
        actions={
          <div className="app-actions">
            <a className="button-secondary" href={appHref}>
              Open App
            </a>
            <button className="button-secondary" type="button" onClick={reload}>
              Refresh
            </button>
          </div>
        }
      >
        {" "}
        <dl className="logbook2-record-meta">
          <div>
            <dt>Started</dt>
            <dd>
              {current.started_at ? (
                <StudioTime value={current.started_at} />
              ) : (
                "Awaiting start"
              )}
            </dd>
          </div>
          <div>
            <dt>Last reported update</dt>
            <dd>
              <StudioTime value={current.observed_at ?? current.requested_at} />
            </dd>
          </div>
          <div>
            <dt>Robot</dt>
            <dd>
              <a href={studioPath("robots", organizationId, current.robot_id)}>
                {robot.name}
              </a>
            </dd>
          </div>
          {person(current.requested_by) && (
            <div>
              <dt>Initiated by</dt>
              <dd>{person(current.requested_by)}</dd>
            </div>
          )}
          <div>
            <dt>Release</dt>
            <dd>{release(current) ?? "Release metadata unavailable"}</dd>
          </div>
          <div>
            <dt>Duration</dt>
            <dd>{duration(current)}</dd>
          </div>
        </dl>
        <details className="logbook-record-technical">
          <summary>Technical details</summary>
          <dl>
            <dt>Run ID</dt>
            <dd>{current.id}</dd>
            <dt>Installation</dt>
            <dd>{current.installation_id}</dd>
            <dt>Requested</dt>
            <dd>{new Date(current.requested_at).toLocaleString()}</dd>
            <dt>Revision</dt>
            <dd>{current.snapshot.revision_id}</dd>
            <dt>Image</dt>
            <dd>{current.snapshot.image}</dd>
          </dl>
        </details>
      </PageHeading>

      {error && (
        <p role="alert" className="app-feedback app-feedback-error">
          {error} · Showing the last known run.
        </p>
      )}
      {running && (
        <Decisions
          organizationId={organizationId}
          robots={robots}
          getToken={token}
          refresh={refresh}
          executionId={current.id}
          role={
            ["owner", "developer", "operator"].includes(workspaceRole ?? "")
              ? workspaceRole
              : "viewer"
          }
        />
      )}
      {current.failure && (
        <p role="alert" className="app-feedback app-feedback-error">
          {current.failure.message}
        </p>
      )}
      <StudioTabs label="Run sections">
        {["Overview", "Output"].map((label) => (
          <a
            key={label}
            href={link(current.id, { tab: label })}
            aria-current={tab === label ? "page" : undefined}
          >
            {label}
          </a>
        ))}
      </StudioTabs>
      {tab === "Overview" ? (
        <>
          <section className="logbook-current">
            <h2>Current situation</h2>
            <p>
              {events.length
                ? eventTitle(events[events.length - 1])
                : current.state}
            </p>
          </section>
          <RunTimeline
            key={current.id}
            preview
            recordedEvents={events}
            focusedRun={clip}
            robots={[robot]}
            runs={[clip]}
            workspace={organizationId}
            token={token}
            robotFilter={robot.id}
          />
          <AppLogbookEvents
            events={events}
            error={eventsError}
            loading={eventsLoading}
            person={person}
          />
          <section
            className="logbook2-section logbook-artifact-section"
            aria-label="Run artifacts"
          >
            <header className="logbook2-section-heading">
              <div>
                <span className="logbook2-section-index">02</span>
                <h2>Artifacts</h2>
              </div>
              <a
                href={appPath(
                  current.snapshot.app_id,
                  organizationId,
                  current.installation_id,
                  "files",
                )}
              >
                Open App Files →
              </a>
            </header>
            <AppFiles
              key={`${current.id}:${refresh}`}
              appId={current.snapshot.app_id}
              organizationId={organizationId}
              installationId={current.installation_id}
              fixedRunId={current.id}
              robotName={robot.name}
              robots={robots}
              runs={[current]}
              token={token}
              live={running}
            />
          </section>
        </>
      ) : (
        <AppRunOutput
          key={current.id}
          run={current}
          token={token}
          refresh={refresh}
        />
      )}
    </main>
  );
}
