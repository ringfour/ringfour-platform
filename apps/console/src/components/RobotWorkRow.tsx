import { resourcePath } from "@/lib/location";
import type { OverviewData } from "./overview-data";
import { RobotResources } from "./RobotResources";
import { StudioTime } from "./StudioTime";
import "./studio.css";

type Machine = OverviewData["machines"][number];
export function RobotWorkRow({
  machine,
  workspace,
  onOpen,
}: {
  machine: Machine;
  workspace: string;
  onOpen?: () => void;
}) {
  const active = machine.runs?.filter((run) =>
    ["queued", "preparing", "running"].includes(run.state),
  );
  const online = machine.status?.online;
  return (
    <article className="studio-machine">
      <div className="studio-identity">
        <h3>
          <a
            onClick={(e) => {
              if (
                onOpen &&
                !e.ctrlKey &&
                !e.metaKey &&
                !e.shiftKey &&
                !e.altKey
              ) {
                e.preventDefault();
                onOpen();
              }
            }}
            href={resourcePath("robots", machine.id, workspace)}
          >
            {machine.name}
          </a>
        </h3>
        <p className="studio-connection" data-online={online === true}>
          {online === true
            ? "Online"
            : online === false
              ? "Offline"
              : "Connectivity unknown"}
        </p>
        {machine.status?.last_seen_at && (
          <small>
            Last seen <StudioTime value={machine.status.last_seen_at} />
          </small>
        )}
        <RobotResources />
      </div>
      <div className="studio-work">
        <small className="studio-work-label">
          {online === false ? "Last known work" : "Current work"}
        </small>
        {active?.length ? (
          active.map((run) => (
            <div key={run.id}>
              <h3>
                {run.appId ? (
                  <a
                    className="studio-app"
                    href={resourcePath("apps", run.appId, workspace)}
                  >
                    {run.app}
                  </a>
                ) : (
                  run.app
                )}{" "}
                {run.version && <small>v{run.version}</small>}
              </h3>
              <a
                aria-label={`View ${run.app} run details`}
                className="studio-run"
                href={resourcePath("runs", run.id, workspace)}
              >
                <span className="studio-state" data-state={run.state}>
                  {run.state}
                </span>
                <span aria-hidden="true"> ↗</span>
              </a>
              {run.activity && <p>{run.activity}</p>}
              <small>
                {run.person && "Started by " + run.person + " · "}
                {run.created_at && <StudioTime value={run.created_at} />}
              </small>
            </div>
          ))
        ) : (
          <>
            <h3>
              {!active
                ? "Run activity unavailable"
                : online === false
                  ? "No active run last reported"
                  : "No app running"}
            </h3>
            {machine.deployment && (
              <small>
                {machine.configured
                  ? machine.configured.app + " v" + machine.configured.version
                  : "Deployment"}{" "}
                · Requested {machine.deployment.desired_state} · Observed{" "}
                {machine.deployment.observed_state}
              </small>
            )}
          </>
        )}
      </div>
    </article>
  );
}
