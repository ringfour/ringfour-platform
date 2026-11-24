import { useAuth0 } from "@auth0/auth0-react";
import { useCallback, useEffect, useState } from "react";
import { resourcePath } from "@/lib/location";
import { useDecisions } from "./Decisions";
import { loadOverview, type OverviewData } from "./overview-data";
import { RobotWorkRow } from "./RobotWorkRow";
import { RunTimeline } from "./RunTimeline";
import { StudioTime } from "./StudioTime";
import "./overview.css";
import "./app-workflows.css";

export function HomePage({
  organizationId,
  workspaceRole,
}: {
  organizationId: string;
  workspaceRole?: string;
}) {
  const { getAccessTokenSilently } = useAuth0();
  const token = useCallback(async () => {
    const value = await getAccessTokenSilently({
      authorizationParams: { audience: import.meta.env.VITE_AUTH0_AUDIENCE },
    });
    if (!value) throw new Error("Sign in required");
    return value;
  }, [getAccessTokenSilently]);
  const [data, setData] = useState<OverviewData | null>(null);
  const [error, setError] = useState("");
  const [refresh, setRefresh] = useState(0);
  const decisions = useDecisions(organizationId, token, refresh);
  // biome-ignore lint/correctness/useExhaustiveDependencies: Refresh explicitly requests a new workspace snapshot.
  useEffect(() => {
    if (!organizationId) return;
    let live = true;
    let timer: number | undefined;
    setData(null);
    setError("");
    const load = async () => {
      try {
        const snapshot = await loadOverview(organizationId, token);
        if (live) {
          setData(snapshot);
          setError("");
        }
      } catch (cause) {
        if (live)
          setError(
            cause instanceof Error
              ? cause.message
              : "Workspace activity is unavailable.",
          );
      } finally {
        if (live) timer = window.setTimeout(load, 30000);
      }
    };
    void load();
    return () => {
      live = false;
      window.clearTimeout(timer);
    };
  }, [organizationId, token, refresh]);
  const failed = data?.issues ?? [];
  const attentionCount = failed.length + (decisions.interactions?.length ?? 0);
  const online =
    data?.machines.filter((robot) => robot.status?.online === true).length ?? 0;
  const active = data?.active ?? [];
  const onlineRuns = active.filter(
    (run) =>
      data?.machines.find((machine) => machine.id === run.robot_id)?.status
        ?.online === true,
  );
  const offlineRuns = active.filter(
    (run) =>
      data?.machines.find((machine) => machine.id === run.robot_id)?.status
        ?.online !== true,
  );
  return (
    <main className="ops-overview home-page">
      <header className="ops-header">
        <div>
          <h1>Home</h1>
          <p className="ops-summary">
            {data ? (
              <>
                {decisions.interactions === null ? (
                  "Checking pending decisions…"
                ) : attentionCount ? (
                  <a href="#home-attention">
                    {attentionCount} item{attentionCount === 1 ? "" : "s"} need
                    attention
                  </a>
                ) : (
                  "No pending attention"
                )}{" "}
                ·{" "}
                <a href="#home-robots">
                  {data.machines.length} robot
                  {data.machines.length === 1 ? "" : "s"} · {online} online
                </a>{" "}
                ·{" "}
                <a href={resourcePath("logbook", "", organizationId)}>
                  {onlineRuns.length} reported running · {offlineRuns.length}{" "}
                  last known
                </a>
              </>
            ) : error ? (
              "Workspace status unavailable"
            ) : (
              "Loading workspace status…"
            )}
          </p>
        </div>
        <div className="ops-refresh">
          <span>
            {data && (
              <>
                Updated <StudioTime value={data.updated.toISOString()} />
              </>
            )}
          </span>
          <button
            type="button"
            className="button-secondary"
            onClick={() => setRefresh((x) => x + 1)}
          >
            Refresh
          </button>
        </div>
      </header>
      {error && (
        <p role="alert" className="app-feedback app-feedback-error">
          {error}
          {data && " Showing the last complete snapshot."}
        </p>
      )}
      <div className="home-grid">
        <section className="app-panel home-attention" id="home-attention">
          <h2>Needs attention</h2>
          {decisions.interactionError && (
            <p role="alert">{decisions.interactionError}</p>
          )}
          {decisions.interactions?.map((item) => (
            <article className="home-attention-row" key={item.request_id}>
              <span className="app-eyebrow">
                Human decision ·{" "}
                {data?.robots.find((r) => r.id === item.robot_id)?.name ??
                  "Robot"}
              </span>
              <h3>{item.prompt}</h3>
              <a
                href={resourcePath(
                  "logbook",
                  item.execution_id,
                  organizationId,
                )}
              >
                Inspect App run ↗
              </a>
              {item.response?.status === "submitted" ? (
                <p>Response submitted; awaiting device confirmation.</p>
              ) : (
                <div className="app-actions">
                  {item.choices.map((choice) => (
                    <button
                      className="button-secondary"
                      type="button"
                      key={choice.value}
                      disabled={
                        workspaceRole === "viewer" ||
                        !!decisions.interactionError ||
                        !!decisions.submittingInteraction
                      }
                      onClick={() => void decisions.respond(item, choice.value)}
                    >
                      {choice.label}
                    </button>
                  ))}
                </div>
              )}
            </article>
          ))}
          {failed.map((issue) => (
            <article className="home-attention-row" key={issue.id}>
              <span className="app-eyebrow">App run {issue.run.state}</span>
              <h3>
                {issue.app} · {issue.robot}
              </h3>
              <p>
                {issue.last_error?.message ?? "The latest run needs review."}
              </p>
              <a href={resourcePath("logbook", issue.id, organizationId)}>
                Inspect run ↗
              </a>
            </article>
          ))}
          {decisions.interactions === null && (
            <p>
              {decisions.interactionError
                ? "Pending decisions are unavailable."
                : "Checking pending decisions…"}
            </p>
          )}
          {data && decisions.interactions !== null && !attentionCount && (
            <p>No failed or interrupted runs, and no pending decisions.</p>
          )}
          {!data && !decisions.interactions && (
            <p>
              {error || decisions.interactionError
                ? "Attention information is unavailable."
                : "Loading attention items…"}
            </p>
          )}
        </section>
        <section className="app-panel home-running">
          <header>
            <h2>Active and last-known App runs</h2>
            <a href={resourcePath("logbook", "", organizationId)}>
              Open Logbook →
            </a>
          </header>
          {active.length ? (
            active.map((run) => (
              <article className="home-run-row" key={run.id}>
                <div>
                  <strong>{run.app}</strong>
                  <p>
                    {run.robot.name} ·{" "}
                    {data?.machines.find(
                      (machine) => machine.id === run.robot_id,
                    )?.status?.online === true
                      ? `Reported ${run.state}`
                      : `Last reported ${run.state} while offline`}
                  </p>
                  <small>
                    {run.person ? `Started by ${run.person} · ` : ""}
                    <StudioTime value={run.created_at} />
                  </small>
                </div>
                <a href={resourcePath("logbook", run.id, organizationId)}>
                  View run ↗
                </a>
              </article>
            ))
          ) : (
            <p>{data ? "No Apps are running." : "Loading App runs…"}</p>
          )}
        </section>
        <section className="home-robots" id="home-robots">
          <header className="home-section-heading">
            <h2>Workspace robots</h2>
            <a href={resourcePath("robots", "", organizationId)}>
              View robots →
            </a>
          </header>
          {data?.machines.length ? (
            data.machines.map((robot) => (
              <RobotWorkRow
                key={robot.id}
                machine={robot}
                workspace={organizationId}
              />
            ))
          ) : (
            <div className="app-panel">
              <p>
                {data
                  ? "No robots are enrolled in this workspace."
                  : "Loading robots…"}
              </p>
            </div>
          )}
        </section>
        {data && (
          <RunTimeline
            preview
            robots={data.robots}
            runs={data.runs}
            workspace={organizationId}
            token={token}
          />
        )}
      </div>
    </main>
  );
}
