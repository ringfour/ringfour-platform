// Shared robot card for Home and the workspace robot collection.

import { StudioTime } from "@/components/StudioTime";
import type { OverviewData } from "@/features/overview/overview-data";
import { appPath, resourcePath, studioPath } from "@/lib/location";
import { runStateLabel } from "@/views/RunStatus";
import { RobotArtwork } from "./RobotArtwork";

export function RobotWorkRow({
  machine,
  workspace,
  onOpen,
}: {
  machine: OverviewData["machines"][number];
  workspace: string;
  onOpen?: () => void;
}) {
  const active = machine.runs?.find((run) =>
    ["accepted", "running"].includes(run.state),
  );
  const latest = machine.runs?.reduce<
    NonNullable<typeof machine.runs>[number] | undefined
  >(
    (recent, run) =>
      ["accepted", "running"].includes(run.state)
        ? recent
        : !recent || run.updated_at > recent.updated_at
          ? run
          : recent,
    undefined,
  );
  const online = machine.status?.online;
  const inventory =
    machine.status?.inventory?.current ??
    machine.status?.inventory?.last_successful;
  const href = resourcePath("robots", machine.id, workspace);
  const open = (event: React.MouseEvent<HTMLAnchorElement>) => {
    if (
      onOpen &&
      !event.ctrlKey &&
      !event.metaKey &&
      !event.shiftKey &&
      !event.altKey &&
      event.button === 0
    ) {
      event.preventDefault();
      onOpen();
    }
  };
  return (
    <article className="robot-card studio-surface">
      <a
        href={href}
        onClick={open}
        aria-label={`Open ${machine.name}`}
        className="robot-card-image-link"
      >
        <RobotArtwork robot={machine} className="robot-card-image" />
      </a>
      <div className="robot-card-content">
        <h3 className="robot-card-title">
          <a href={href} onClick={open}>
            {machine.name}
          </a>{" "}
          <span className="robot-card-state">
            {active ? (
              <>
                {online === true
                  ? active.state === "running"
                    ? "is running"
                    : "is starting"
                  : "last reported"}{" "}
                <a
                  className="text-foreground hover:text-signal-text"
                  href={appPath(
                    active.appId,
                    workspace,
                    active.installation_id,
                  )}
                >
                  {active.app}
                </a>
              </>
            ) : online === true && machine.runs ? (
              "is idle"
            ) : online === false ? (
              "is offline"
            ) : online === true ? (
              "is online · run status unavailable"
            ) : (
              "· Connectivity unknown"
            )}
          </span>
        </h3>
        <p className="text-sm text-muted-foreground">
          {inventory?.source?.profile?.name ?? "Profile not reported"}
        </p>
        <div className="robot-card-metadata">
          {!active && latest && (
            <p>
              <a
                className="hover:text-signal-text hover:underline"
                href={studioPath("logbook", workspace, latest.id)}
              >
                <strong>Last run</strong>{" "}
                {runStateLabel(latest.state).toLowerCase()} · {latest.app}
              </a>
            </p>
          )}
          {machine.status?.last_seen_at && (
            <p>
              <strong>Last seen</strong>{" "}
              <StudioTime value={machine.status.last_seen_at} />
            </p>
          )}
        </div>
      </div>
      <div className="robot-card-actions">
        {active ? (
          <div className="flex items-center gap-3">
            <a
              aria-label={`View ${active.app} run details`}
              href={studioPath("logbook", workspace, active.id)}
              className="text-xs text-muted-foreground"
            >
              Run details
            </a>
            <a
              href={appPath(active.appId, workspace, active.installation_id)}
              className="button-secondary"
            >
              Open {active.app}
            </a>
          </div>
        ) : online === true && machine.runs ? (
          <a
            className="button-primary"
            href={studioPath(
              machine.installedApps.length ? "apps" : "store",
              workspace,
              "",
              { robot: machine.id },
            )}
          >
            Choose an App →
          </a>
        ) : (
          <a
            className="button-secondary"
            href={resourcePath("robots", machine.id, workspace)}
          >
            Inspect robot
          </a>
        )}
      </div>
    </article>
  );
}
