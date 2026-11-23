import { useAuth0 } from "@auth0/auth0-react";
import { useCallback, useEffect, useState } from "react";
import { apiFetchAll } from "@/lib/api";
import { resourcePath } from "@/lib/location";
import { useDecisions } from "./Decisions";
import { loadOverview, type OverviewData } from "./overview-data";
import { RunTimeline } from "./RunTimeline";
import { RobotWorkRow } from "./RobotWorkRow";
import { StudioTime } from "./StudioTime";
import "./overview.css";

const when = (value: string) =>
  new Date(value).toLocaleString(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit",
  });
function waiting(value: string) {
  const minutes = Math.floor((Date.now() - Date.parse(value)) / 60000);
  if (!Number.isFinite(minutes) || minutes < 0) return "";
  if (minutes < 1) return "Just requested";
  return minutes < 60
    ? `Waiting ${minutes} min`
    : `Waiting ${Math.floor(minutes / 60)} hr`;
}
export function DashboardPage({
  onNavigate,
  preferredOrganizationId = "",
}: {
  onNavigate: (view: "identity") => void;
  preferredOrganizationId?: string;
  onOrganizationChange?: (id: string) => void;
}) {
  const { getAccessTokenSilently } = useAuth0();
  const token = useCallback(async () => {
    const value = await getAccessTokenSilently({
      authorizationParams: { audience: import.meta.env.VITE_AUTH0_AUDIENCE },
    });
    if (!value) throw Error("Sign in required");
    return value;
  }, [getAccessTokenSilently]);
  const [org, setOrg] = useState(preferredOrganizationId);
  const [noWorkspace, setNoWorkspace] = useState(false);
  const [review, setReview] = useState("");
  const [refresh, setRefresh] = useState(0);
  const [data, setData] = useState<OverviewData>();
  const [error, setError] = useState("");
  // biome-ignore lint/correctness/useExhaustiveDependencies: Refresh retries the workspace lookup.
  useEffect(() => {
    if (preferredOrganizationId) return;
    let current = true;
    void apiFetchAll<{ id: string }>(token, "/v1/organizations")
      .then((items) => {
        if (current) {
          setOrg(items[0]?.id ?? "");
          setNoWorkspace(!items.length);
        }
      })
      .catch(() => {
        if (current) setError("Workspace information is unavailable.");
      });
    return () => {
      current = false;
    };
  }, [preferredOrganizationId, token, refresh]);
  // biome-ignore lint/correctness/useExhaustiveDependencies: Refresh requests a fresh operational snapshot.
  useEffect(() => {
    if (!org) return;
    let current = true;
    let timer: number | undefined;
    const load = () =>
      loadOverview(org, token)
        .then((snapshot) => {
          if (current) {
            setData(snapshot);
            setError("");
          }
        })
        .catch(() => {
          if (current) setError("Could not update workspace activity.");
        })
        .finally(() => {
          if (current) timer = window.setTimeout(load, 30000);
        });
    void load();
    return () => {
      current = false;
      window.clearTimeout(timer);
    };
  }, [org, token, refresh]);
  const queue = useDecisions(org, token, refresh);
  const decisions = [...(queue.interactions ?? [])].sort(
    (a, b) =>
      (a.deadline ?? "~").localeCompare(b.deadline ?? "~") ||
      a.created_at.localeCompare(b.created_at),
  );
  const linked = new Set(decisions.map((d) => d.execution_id));
  const issues =
    data?.issues?.filter(
      (issue) =>
        !issue.last_execution_id || !linked.has(issue.last_execution_id),
    ) ?? [];
  const count = decisions.length + issues.length;
  const complete =
    !!data?.issues &&
    queue.interactions !== null &&
    !queue.interactionError &&
    !error;
  const active = data?.active ?? [];
  const runPath = (id: string) => resourcePath("runs", id, org);
  const runsPath = runPath("");
  return (
    <main className="ops-overview">
      <header className="ops-header">
        <div>
          <h1>Overview</h1>
          <p className="ops-summary">
            {complete && (
              <>
                <a
                  href="#overview-attention"
                  className={count > 0 ? "ops-summary-alert" : undefined}
                >
                  {count === 0
                    ? "Nothing needs attention"
                    : count === 1
                      ? "1 operation needs attention"
                      : count + " operations need attention"}
                </a>
                .{" "}
              </>
            )}
            {data && (
              <>
                <a href="#overview-machines">
                  {data.machines.filter((m) => m.status?.online).length}{" "}
                  confirmed online
                  {data.machines.some(
                    (m) => typeof m.status?.online !== "boolean",
                  ) && " · connectivity incomplete"}
                </a>{" "}
                ·
                <a href={runsPath + "&group=Active"}>
                  {active.length === 0
                    ? data.machines.some((m) => !m.runs)
                      ? "Run activity incomplete"
                      : "No apps running"
                    : active.length === 1
                      ? "1 active run"
                      : active.length + " active runs"}
                </a>
                .
              </>
            )}
          </p>
        </div>
        <div className="ops-refresh">
          {data && (
            <span>
              Updated <StudioTime value={data.updated.toISOString()} />
            </span>
          )}
          <button
            type="button"
            className="button-secondary"
            onClick={() => setRefresh((v) => v + 1)}
          >
            Refresh
          </button>
        </div>
      </header>
      {error && (
        <p role="alert">
          {error}
          {data && " Showing the last known snapshot."}
        </p>
      )}
      {noWorkspace ? (
        <p>
          Create a workspace to get started.{" "}
          <button
            type="button"
            className="text-action"
            onClick={() => onNavigate("identity")}
          >
            Workspace settings →
          </button>
        </p>
      ) : (
        <div className="ops-layout">
          <aside className="ops-side">
            <section
              id="overview-attention"
              className="ops-attention"
              data-has-attention={count > 0}
            >
              <h2>Needs attention</h2>
              {queue.interactionError && (
                <p role="alert">{queue.interactionError}</p>
              )}
              {data && !data.issues && (
                <p>Deployment information unavailable.</p>
              )}
              {decisions.map((d) => {
                const run = data?.runs.find((r) => r.id === d.execution_id);
                const issue = data?.issues?.find(
                  (i) => i.last_execution_id === d.execution_id,
                );
                return (
                  <article className="ops-situation" key={d.request_id}>
                    <div>
                      <span className="ops-type">Decision required</span>
                      <h3>{d.prompt}</h3>
                      <p>
                        {run?.app ?? "App unavailable"}
                        {run?.version !== undefined &&
                          " · Version " + run.version}{" "}
                        ·{" "}
                        {data?.robots.find((r) => r.id === d.robot_id)?.name ??
                          "Robot unavailable"}
                      </p>
                      <small>
                        {[
                          run?.person && "Started by " + run.person,
                          waiting(d.created_at),
                          d.deadline && "Due " + when(d.deadline),
                        ]
                          .filter(Boolean)
                          .join(" · ")}
                      </small>
                      {issue && (
                        <p>
                          Requested: {issue.desired_state} · Observed:{" "}
                          {issue.observed_state}
                        </p>
                      )}
                    </div>
                    {d.response?.status === "submitted" ? (
                      <a className="ops-primary" href={runPath(d.execution_id)}>
                        View submitted response →
                      </a>
                    ) : (
                      <button
                        className="ops-primary"
                        type="button"
                        aria-expanded={review === d.request_id}
                        onClick={() =>
                          setReview(review === d.request_id ? "" : d.request_id)
                        }
                      >
                        {review === d.request_id
                          ? "Close review"
                          : "Review decision →"}
                      </button>
                    )}
                    {review === d.request_id && !d.response && (
                      <div className="ops-choices">
                        <a href={runPath(d.execution_id)}>Inspect run →</a>
                        {d.choices.map((choice) => (
                          <button
                            type="button"
                            key={choice.value}
                            disabled={
                              !!queue.interactionError ||
                              !!queue.submittingInteraction
                            }
                            onClick={() => void queue.respond(d, choice.value)}
                          >
                            {choice.label}
                          </button>
                        ))}
                      </div>
                    )}
                  </article>
                );
              })}
              {issues.map((d) => (
                <article className="ops-situation" key={d.id}>
                  <div>
                    <span className="ops-type">
                      {d.run?.state === "failed" &&
                      d.observed_state === "failed" &&
                      d.desired_state !== "running"
                        ? "Run failed"
                        : "Deployment mismatch"}
                    </span>
                    <h3>
                      {d.robot}
                      {d.observed_state === "failed"
                        ? " reported a failure"
                        : d.last_error
                          ? " needs deployment review"
                          : d.desired_state === "running"
                            ? " is waiting to run"
                            : " is still running"}
                    </h3>
                    <p>
                      Requested: {d.desired_state} · Observed:{" "}
                      {d.observed_state}
                    </p>
                    <small>
                      {d.app ?? "App unavailable"}
                      {d.version !== undefined && " · Version " + d.version}
                      {d.run?.person && " · Started by " + d.run.person}
                      {d.updated_at && " · Updated " + when(d.updated_at)}
                    </small>
                  </div>
                  <a
                    className="ops-primary"
                    href={
                      resourcePath("robots", d.robot_id, org) +
                      "&tab=Deployment"
                    }
                  >
                    Inspect deployment →
                  </a>
                </article>
              ))}
              {!count && (
                <p>
                  {complete
                    ? "Nothing needs your attention."
                    : error || queue.interactionError || (data && !data.issues)
                      ? "Attention status unavailable."
                      : "Checking current conditions…"}
                </p>
              )}
            </section>
            <section className="ops-outcomes">
              <header>
                <h2>Recent outcomes</h2>
                <a href={`${runsPath}&group=All`}>All runs →</a>
              </header>
              {data?.recent.length ? (
                <ul>
                  {data.recent.map((run) => (
                    <li key={run.id}>
                      <div>
                        <h3>{run.app}</h3>
                        <small>
                          {run.robot.name}
                          {run.person && ` · Started by ${run.person}`}
                        </small>
                        {(run.error?.message ||
                          run.activity ||
                          !!run.evidence) && (
                          <p>
                            {[
                              run.error?.message ?? run.activity,
                              run.evidence
                                ? `${run.evidence} evidence ${run.evidence === 1 ? "file" : "files"}`
                                : "",
                            ]
                              .filter(Boolean)
                              .join(" · ")}
                          </p>
                        )}
                      </div>
                      <span className="ops-outcome" data-state={run.state}>
                        {run.state === "succeeded"
                          ? "Execution succeeded"
                          : run.state === "failed"
                            ? "Failed"
                            : "Cancelled"}
                      </span>
                      <StudioTime value={run.updated_at} />
                      <a
                        href={runPath(run.id)}
                        aria-label={`View ${run.app} run details`}
                      >
                        →
                      </a>
                    </li>
                  ))}
                </ul>
              ) : (
                <p>
                  {data
                    ? "No finished runs yet."
                    : error
                      ? "Recent outcomes unavailable."
                      : "Loading outcomes…"}
                </p>
              )}
            </section>
          </aside>
          <div className="ops-main">
            <section id="overview-machines">
              <header className="ops-machines-heading">
                <h2>Robots and current work</h2>
              </header>
              {data ? (
                data.machines.length ? (
                  data.machines.map((machine) => (
                    <RobotWorkRow
                      key={machine.id}
                      machine={machine}
                      workspace={org}
                    />
                  ))
                ) : (
                  <p>No robots in this workspace.</p>
                )
              ) : (
                <p>
                  {error ? "Robot information unavailable." : "Loading robots…"}
                </p>
              )}
            </section>
            {data && (
              <RunTimeline
                preview
                robots={data.robots}
                runs={data.runs}
                workspace={org}
                token={token}
              />
            )}
          </div>
        </div>
      )}
    </main>
  );
}
