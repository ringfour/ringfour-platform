import { AppOperationStatus } from "@/components/AppOperationStatus";
import { PageHeading } from "@/components/PageHeading";
import { StudioTime } from "@/components/StudioTime";
import { AppArtwork } from "@/features/apps/views/AppArtwork";
import { AppVariant } from "@/features/apps/views/AppVariant";
import {
  appPath,
  resourcePath,
  robotCredentialsPath,
  studioPath,
} from "@/lib/location";
import { agentUpdate, daemonUpdate } from "@/lib/robot-status";
import { RunHistoryRow } from "@/views/RunHistoryRow";
import { RunStatus } from "@/views/RunStatus";
import { StudioPage, StudioSection } from "@/views/StudioPage";
import { InstallationCompatibility } from "./InstallationCompatibility";
import { RobotAppearanceEditor } from "./RobotAppearanceEditor";
import { RobotArtwork } from "./RobotArtwork";
import { RobotCapabilities } from "./RobotCapabilities";
import { RobotDevelopment } from "./RobotDevelopment";
import { type RobotDetailScope, useRobotDetails } from "./useRobotDetails";
import "./robots.css";
export function RobotDetailPage(props: RobotDetailScope) {
  const { robotId } = props;
  const {
    token,
    robot,
    status,
    installations,
    runs,
    events,
    apps,
    revisions,
    operationId,
    error,
    inventory,
    canRun,
    activeRun,
    action,
    refreshAfterOperation,
  } = useRobotDetails(props);
  if (!robot)
    return (
      <main className="app-workflow-page">
        <PageHeading
          title={error ? "Robot unavailable" : "Opening robot…"}
          description={
            <span role={error ? "alert" : "status"}>
              {error || "Loading robot details…"}
            </span>
          }
        />
      </main>
    );
  const daemonState = daemonUpdate(status ?? undefined);
  const agentState = agentUpdate(status ?? undefined);
  const agentVersion =
    status?.inventory?.state === "available"
      ? status.inventory.current?.source.agent_version
      : null;
  return (
    <StudioPage
      heading={
        <PageHeading
          className="robot-detail-header"
          title={robot.name}
          artwork={
            <RobotArtwork robot={robot} className="robot-detail-image" />
          }
          metadata={
            <span
              className={`rf-badge ${status?.connectivity === "online" ? "text-success" : "text-muted-foreground"}`}
            >
              {status?.connectivity === "online"
                ? "Online"
                : status?.connectivity === "offline"
                  ? "Offline"
                  : "Connectivity unknown"}
            </span>
          }
          description={
            <>
              <p>
                {inventory?.source?.profile?.name ??
                  "Robot profile unavailable"}
              </p>
              <p>
                <strong>Current App</strong>{" "}
                {activeRun ? (
                  <a
                    className="text-signal-text"
                    href={appPath(
                      activeRun.snapshot.app_id,
                      robot.organization_id,
                      activeRun.installation_id,
                    )}
                  >
                    {apps[activeRun.snapshot.app_id]?.name ?? "App"}
                  </a>
                ) : (
                  "None running"
                )}
              </p>
              {status?.last_seen_at && (
                <p>
                  <strong>Last seen</strong>{" "}
                  <StudioTime value={status.last_seen_at} />
                </p>
              )}
            </>
          }
          actions={
            <div className="flex flex-wrap gap-2">
              <a
                className="button-primary"
                href={studioPath("store", robot.organization_id, "", {
                  robot: robotId,
                })}
              >
                Choose an App
              </a>
              <button
                className="button-secondary"
                type="button"
                onClick={refreshAfterOperation}
              >
                Refresh
              </button>
            </div>
          }
        >
          <section aria-label="Device details">
            <dl className="robot-device-facts">
              {[
                [
                  "Architecture",
                  status?.platform?.architecture ?? "Not reported",
                ],
                ["Operating system", status?.platform?.os ?? "Not reported"],
                [
                  "App protocol",
                  status?.support?.replaceAll("_", " ") ?? "Not reported",
                ],
                [
                  "Profile model",
                  inventory?.source?.profile?.model ?? "Not reported",
                ],
                [
                  "Profile version",
                  inventory?.source?.profile?.version ?? "Not reported",
                ],
              ].map(([label, value]) => (
                <div key={label}>
                  <dt>{label}</dt>
                  <dd>{value}</dd>
                </div>
              ))}
              <div className="robot-software-fact">
                <dt>Daemon software</dt>
                <dd>
                  <span>Installed {status?.daemon_version ?? "Unknown"}</span>
                  <span>
                    Available {status?.recommended_daemon_version ?? "Unknown"}
                  </span>
                  <span>{daemonState}</span>
                  {daemonState === "update available" && (
                    <a href="https://github.com/ringfour/ringfour-platform/blob/main/deployment/README.md">
                      Update instructions
                    </a>
                  )}
                </dd>
              </div>
              <div className="robot-software-fact">
                <dt>Agent software</dt>
                <dd>
                  <span>Installed {agentVersion ?? "Unknown"}</span>
                  <span>
                    Available {status?.recommended_agent_version ?? "Unknown"}
                  </span>
                  <span>{agentState}</span>
                  {agentState === "update available" && (
                    <a href="https://github.com/ringfour/ringfour-agent#integrator-update">
                      Integrator update instructions
                    </a>
                  )}
                </dd>
              </div>
              <div>
                <dt>Device ID</dt>
                <dd>{robot.id}</dd>
              </div>
            </dl>
          </section>
        </PageHeading>
      }
    >
      {error && (
        <p role="alert" className="text-destructive">
          {error}
        </p>
      )}
      {operationId && (
        <AppOperationStatus
          operationId={operationId}
          token={token}
          onTerminal={refreshAfterOperation}
        />
      )}
      {["owner", "developer"].includes(props.workspaceRole ?? "") && (
        <RobotDevelopment
          robotId={robotId}
          token={token}
          installations={installations}
          apps={apps}
          activeRun={!!activeRun}
          onChange={refreshAfterOperation}
        />
      )}
      <div className="studio-split">
        <StudioSection title="Installed Apps" titleId="installed-apps-heading">
          {!installations.length ? (
            <p className="app-muted">
              No App installations are associated with this robot.
            </p>
          ) : (
            <ul className="robot-installations">
              {installations.map((item) => {
                const app = apps[item.app_id];
                const active = runs.find(
                  (run) =>
                    run.installation_id === item.id &&
                    ["accepted", "running"].includes(run.state),
                );
                const manifest = item.installed
                  ? revisions[item.installed.revision_id]?.manifest
                  : undefined;
                return (
                  <li key={item.id} className="robot-installation">
                    <AppArtwork
                      app={app ?? { name: "", app_key: "" }}
                      className="h-10 w-10"
                    />
                    <div className="min-w-0">
                      <a
                        className="font-display font-medium hover:text-signal-text"
                        href={appPath(
                          item.app_id,
                          robot.organization_id,
                          item.id,
                        )}
                      >
                        {app?.name ?? "App"}
                      </a>
                      <div className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted-foreground">
                        <span>
                          {manifest?.release
                            ? `Release ${manifest.release}`
                            : item.installed
                              ? "Release unavailable"
                              : item.candidate
                                ? `Install ${item.candidate.state}`
                                : "Not installed"}
                        </span>
                        {active ? (
                          <RunStatus state={active.state} />
                        ) : (
                          item.installed && <span>Idle</span>
                        )}
                      </div>
                      {manifest?.display?.description && (
                        <p className="mt-2 break-words text-sm text-muted-foreground">
                          {manifest.display.description}
                        </p>
                      )}
                      {app && (
                        <AppVariant app={app} peers={Object.values(apps)} />
                      )}
                      {item.installed && (
                        <InstallationCompatibility
                          robot={robot}
                          revisionId={item.installed.revision_id}
                          token={token}
                        />
                      )}
                    </div>
                    <div className="robot-installation-actions">
                      <a
                        className="text-sm text-muted-foreground"
                        href={appPath(
                          item.app_id,
                          robot.organization_id,
                          item.id,
                          "settings",
                        )}
                      >
                        Settings
                      </a>
                      {active && (
                        <a
                          className="text-sm text-signal-text"
                          href={studioPath(
                            "logbook",
                            robot.organization_id,
                            active.id,
                          )}
                        >
                          Open run
                        </a>
                      )}
                      {canRun && item.installed && (
                        <button
                          className={
                            active ? "button-danger" : "button-primary"
                          }
                          type="button"
                          disabled={
                            status?.connectivity !== "online" ||
                            !!(
                              item.candidate &&
                              item.candidate.state !== "failed"
                            ) ||
                            (!active && !!activeRun)
                          }
                          onClick={() =>
                            void action(item, active ? "stop" : "start")
                          }
                        >
                          {active ? "Stop" : "Start"}
                        </button>
                      )}
                    </div>
                  </li>
                );
              })}
            </ul>
          )}
        </StudioSection>
        <StudioSection
          title="Capabilities"
          titleId="capabilities-heading"
          className="robot-inventory-column"
        >
          <RobotCapabilities status={status ?? undefined} />
        </StudioSection>
      </div>
      <StudioSection
        title="Recent App runs"
        titleId="robot-recent-runs"
        className="robot-run-history"
      >
        {[...runs]
          .sort((a, b) => b.requested_at.localeCompare(a.requested_at))
          .slice(0, 10)
          .map((run) => (
            <RunHistoryRow
              key={run.id}
              title={apps[run.snapshot.app_id]?.name ?? "App"}
              detail={
                run.snapshot.development
                  ? `Development · source generation ${run.snapshot.development.generation}`
                  : undefined
              }
              openOnRow
              state={run.state}
              time={run.requested_at}
              href={resourcePath("runs", run.id, robot.organization_id)}
            />
          ))}
        {!runs.length && <p className="app-muted">No App runs yet.</p>}
      </StudioSection>
      {props.workspaceRole === "owner" && (
        <StudioSection title="Appearance" titleId="robot-appearance-heading">
          <RobotAppearanceEditor
            key={robot.id}
            robot={robot}
            token={token}
            onSaved={refreshAfterOperation}
          />
        </StudioSection>
      )}
      <StudioSection
        title="Access"
        titleId="robot-access-heading"
        description="Manage the credentials used by this robot’s Daemon."
        actions={
          <a
            className="button-secondary"
            href={robotCredentialsPath(robotId, robot.organization_id)}
          >
            Device access
          </a>
        }
      />
      <details className="studio-section">
        <summary>Activity</summary>
        <div className="mt-4 divide-y divide-border">
          {events.slice(0, 30).map((event) => (
            <article key={event.event_id} className="py-3 text-sm">
              <strong>{event.type}</strong>
              <p className="text-xs text-muted-foreground">
                <StudioTime value={event.occurred_at} /> ·{" "}
                {typeof event.payload.message === "string"
                  ? event.payload.message
                  : "App event"}
              </p>
            </article>
          ))}
        </div>
      </details>
    </StudioPage>
  );
}
