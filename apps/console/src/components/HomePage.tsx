import { useAuth0 } from "@auth0/auth0-react";
import { useCallback, useEffect, useRef, useState } from "react";
import { readLiveRuns } from "@/lib/apps";
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
  const loadedOrganization = useRef("");
  const decisions = useDecisions(organizationId, token, refresh);
  // biome-ignore lint/correctness/useExhaustiveDependencies: Refresh explicitly requests a new workspace snapshot.
  useEffect(() => {
    if (!organizationId) return;
    let live = true;
    let timer: number | undefined;
    if (loadedOrganization.current !== organizationId) {
      loadedOrganization.current = organizationId;
      setData(null);
      setError("");
    }
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

  useEffect(() => {
    if (!organizationId) return;
    let live = true;
    let timer: number | undefined;
    const poll = async () => {
      try {
        const incoming = await readLiveRuns(
          token,
          `/v1/organizations/${encodeURIComponent(organizationId)}/app-runs`,
        );
        if (live) {
          setData((current) => {
            if (!current) return current;
            const known = new Map(current.runs.map((run) => [run.id, run]));
            const rows = incoming.map((run) => {
              const prior = known.get(run.id);
              const robot = current.robots.find((item) => item.id === run.robot_id);
              if (!robot) return prior;
              const app = current.importedApps.find((item) => item.id === run.snapshot.app_id)?.name ??
                current.machines.find((item) => item.id === run.robot_id)?.installedApps.find((item) => item.appId === run.snapshot.app_id)?.name ??
                prior?.app ?? "App";
              return {
                ...prior,
                ...run,
                robot,
                app,
                appId: run.snapshot.app_id,
                version: prior?.version,
                created_at: run.requested_at,
                updated_at: run.observed_at ?? run.started_at ?? run.requested_at,
                error: run.failure ? { message: run.failure.message } : null,
              };
            }).filter((run): run is NonNullable<typeof run> => Boolean(run));
            const active = rows.filter((run) => ["accepted", "running"].includes(run.state));
            const unchanged = rows.length === current.runs.length && rows.every((run) => {
              const previous = known.get(run.id);
              return previous?.state === run.state && previous?.observed_at === run.observed_at;
            });
            if (unchanged) return current;
            const machines = current.machines.map((machine) => ({
              ...machine,
              runs: rows.filter((run) => run.robot_id === machine.id),
            }));
            const recent = rows
              .filter((run) => !["accepted", "running"].includes(run.state))
              .sort((a, b) => b.updated_at.localeCompare(a.updated_at))
              .slice(0, 5);
            const issues = rows
              .filter((run) => run.state === "failed" || run.state === "interrupted")
              .map((run) => ({
                id: run.id,
                robot_id: run.robot_id,
                robot: run.robot.name,
                last_execution_id: run.id,
                run,
                app: run.app,
                version: run.version,
                desired_state: "running" as const,
                observed_state: run.state,
                updated_at: run.updated_at,
                last_error: run.error,
              }));
            return { ...current, machines, runs: rows, active, recent, issues, updated: new Date() };
          });
        }
      } catch {
        // Keep the last complete snapshot visible while the live read retries.
      } finally {
        if (live) timer = window.setTimeout(poll, document.visibilityState === "visible" ? 1200 : 5000);
      }
    };
    timer = window.setTimeout(poll, 1200);
    return () => {
      live = false;
      window.clearTimeout(timer);
    };
  }, [organizationId, token]);
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
        ?.online === false,
  );
  const unknownRuns = active.filter(
    (run) =>
      data?.machines.find((machine) => machine.id === run.robot_id)?.status
        ?.online === undefined,
  );
  const activeByApp = new Map<string, (typeof active)[number][]>();
  for (const run of active) {
    const group = activeByApp.get(run.appId) ?? [];
    group.push(run);
    activeByApp.set(run.appId, group);
  }
  const isEmptyWorkspace = Boolean(
    data &&
      data.machines.length === 0 &&
      active.length === 0 &&
      attentionCount === 0,
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
                  {onlineRuns.length} reported active · {offlineRuns.length}{" "}
                  last known offline · {unknownRuns.length} status unavailable
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
      {isEmptyWorkspace ? (
        <section
          className="app-panel home-onboarding"
          aria-labelledby="home-onboarding-title"
        >
          <p className="app-eyebrow">Workspace setup</p>
          <h2 id="home-onboarding-title">Connect a robot and choose an App</h2>
          <p>
            Enroll a robot to see its status here, then install an App for that
            robot. Installing an App does not start it.
          </p>
          <div className="app-actions">
            <a
              className="button-primary"
              href={resourcePath("robots", "", organizationId)}
            >
              Open Robots
            </a>
            {data?.importedApps.length ? (
              <a
                className="button-secondary"
                href={resourcePath("apps", "", organizationId)}
              >
                Choose an imported App
              </a>
            ) : (
              <a
                className="button-secondary"
                href={resourcePath("app-store", "", organizationId)}
              >
                Browse App Store
              </a>
            )}
          </div>
        </section>
      ) : (
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
                        onClick={() =>
                          void decisions.respond(item, choice.value)
                        }
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
              <h2>Apps in use</h2>
              <a href={resourcePath("logbook", "", organizationId)}>
                Open Logbook →
              </a>
            </header>
            {activeByApp.size ? (
              [...activeByApp].map(([appId, appRuns]) => (
                <article className="home-run-row home-app-group" key={appId}>
                  <div>
                    <strong>
                      <a href={resourcePath("apps", appId, organizationId)}>
                        {appRuns[0].app}
                      </a>
                    </strong>
                    <div className="home-app-assignments">
                      {appRuns.map((run) => {
                        const online = data?.machines.find(
                          (machine) => machine.id === run.robot_id,
                        )?.status?.online;
                        return (
                          <p key={run.id}>
                            <a
                              href={`${resourcePath("apps", appId, organizationId)}&installation=${encodeURIComponent(run.installation_id)}`}
                            >
                              {run.robot.name}
                            </a>
                            {" · "}
                            {online === true
                              ? `Reported ${run.state}`
                              : online === false
                                ? `Last reported ${run.state} while offline`
                                : "Execution status unavailable"}
                            {" · "}
                            <a
                              href={resourcePath(
                                "logbook",
                                run.id,
                                organizationId,
                              )}
                            >
                              View run
                            </a>
                          </p>
                        );
                      })}
                    </div>
                  </div>
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
      )}
    </main>
  );
}
