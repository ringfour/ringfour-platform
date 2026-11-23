import { StudioTime } from "./StudioTime";
import { useAuth0 } from "@auth0/auth0-react";
import { useCallback, useEffect, useState } from "react";
import { apiFetch, apiFetchAll } from "@/lib/api";
import { resourcePath, setQuery, useLocation } from "@/lib/location";

type Robot = {
  id: string;
  organization_id: string;
  name: string;
  created_at: string;
  enrolled_at: string | null;
  profile_ref: string | null;
};
type Status = {
  online: boolean;
  last_seen_at: string | null;
  daemon_version: string | null;
  protocol_version: number | null;
};
type Deployment = {
  id: string;
  robot_id: string;
  selected_revision_id: string | null;
  candidate_revision_id: string | null;
  observed_state: string;
  desired_state: string;
  last_error: { code: string; message: string } | null;
  last_operation_id?: string | null;
  last_execution_id: string | null;
  updated_at: string;
};
type Execution = {
  id: string;
  deployment_id: string | null;
  mission_revision_id: string | null;
  state: string;
  created_at: string;
  updated_at: string;
  exit_code: number | null;
  error: { code: string; message: string } | null;
};
type Mission = { id: string; name: string };
type Revision = { id: string; version: number };
const describe = (error: unknown) =>
  error instanceof Error ? error.message : "Request failed";

export function RobotDetailPage({
  robotId,
  onBack,
  onManage,
  role,
}: {
  robotId: string;
  onBack: () => void;
  role?: string;
  onManage: () => void;
}) {
  const tab = useLocation().searchParams.get("tab") ?? "Activity";
  const { getAccessTokenSilently } = useAuth0();
  const getToken = useCallback(async () => {
    const token = await getAccessTokenSilently({
      authorizationParams: { audience: import.meta.env.VITE_AUTH0_AUDIENCE },
    });
    if (!token) throw new Error("No access token available");
    return token;
  }, [getAccessTokenSilently]);
  const [robot, setRobot] = useState<Robot | null>(null);
  const [status, setStatus] = useState<Status | null>(null);
  const [deployment, setDeployment] = useState<Deployment | null>(null);
  const [revisionNames, setRevisionNames] = useState<Record<string, string>>(
    {},
  );
  const [runs, setRuns] = useState<Execution[] | null>(null);
  const [error, setError] = useState("");

  const [review, setReview] = useState<"stop" | "restart" | null>(null);
  const [notice, setNotice] = useState("");
  const [busy, setBusy] = useState(false);
  const [refresh, setRefresh] = useState(0);
  // Explicit refresh requests a new snapshot.
  // biome-ignore lint/correctness/useExhaustiveDependencies: refresh triggers a new snapshot
  useEffect(() => {
    let cancelled = false;
    setError("");
    async function load() {
      const current = (await apiFetch(getToken, `/v1/robots/${robotId}`).then(
        (r) => r.json(),
      )) as Robot;
      const [presence, history, deployments] = await Promise.all([
        apiFetch(getToken, `/v1/robots/${robotId}/status`).then((r) =>
          r.json(),
        ) as Promise<Status>,
        apiFetchAll<Execution>(getToken, `/v1/robots/${robotId}/executions`),
        apiFetchAll<Deployment>(
          getToken,
          `/v1/organizations/${current.organization_id}/deployments`,
        ),
      ]);
      void apiFetchAll<Mission>(
        getToken,
        `/v1/organizations/${current.organization_id}/missions`,
      )
        .then(async (missions) => {
          const pages = await Promise.all(
            missions.map(async (mission) => ({
              mission,
              revisions: await apiFetchAll<Revision>(
                getToken,
                `/v1/missions/${mission.id}/revisions`,
              ),
            })),
          );
          const names: Record<string, string> = {};
          for (const page of pages)
            for (const item of page.revisions)
              names[item.id] = `${page.mission.name} v${item.version}`;
          if (!cancelled) setRevisionNames(names);
        })
        .catch(() => {
          /* IDs remain available if names cannot be resolved. */
        });
      if (!cancelled) {
        setRobot(current);
        setStatus(presence);
        setRuns(history.reverse());
        setDeployment(
          deployments.find((item) => item.robot_id === robotId) ?? null,
        );
      }
    }
    void load().catch((e) => {
      if (!cancelled) setError(describe(e));
    });
    return () => {
      cancelled = true;
    };
  }, [robotId, getToken, refresh]);
  const currentRuns = runs?.filter((item) =>
    ["queued", "preparing", "running"].includes(item.state),
  );
  async function action(kind: "restart" | "stop") {
    if (!deployment || busy) return;
    setReview(null);
    setBusy(true);
    setError("");
    setNotice("");
    try {
      const result = (await apiFetch(
        getToken,
        `/v1/deployments/${deployment.id}/${kind}`,
        { method: "POST", headers: { "Idempotency-Key": crypto.randomUUID() } },
      ).then((r) => r.json())) as {
        operation_id: string;
        execution_id: string | null;
      };
      setNotice(
        `${kind === "restart" ? "Restart" : "Stop"} accepted. Operation ${result.operation_id}${result.execution_id ? `; execution ${result.execution_id}` : ""}.`,
      );
      setRefresh((value) => value + 1);
    } catch (e) {
      setError(describe(e));
    } finally {
      setBusy(false);
    }
  }
  return (
    <main className="dashboard-page robot-detail-page">
      <button type="button" className="text-action" onClick={onBack}>
        ← Back to robots
      </button>
      <div className="dashboard-heading">
        <div>
          <h1>{robot?.name ?? "Robot"}</h1>
          {status && (
            <p className="robot-header-status">
              <span className="robot-presence" data-online={status.online}>
                {status.online ? "Online" : "Offline"}
              </span>
              {status.last_seen_at && (
                <>
                  {" "}
                  · Last seen <StudioTime value={status.last_seen_at} />
                </>
              )}
            </p>
          )}
        </div>
        <div className="robot-actions">
          <button className="button-primary" type="button" onClick={onManage}>
            Manage robot
          </button>
          <button
            className="button-secondary"
            type="button"
            onClick={() => setRefresh((value) => value + 1)}
          >
            Refresh
          </button>
        </div>
      </div>
      {error && (
        <p role="alert" className="notice-error">
          {error}
        </p>
      )}
      {notice && <p role="status">{notice}</p>}
      {robot && (
        <>
          <div className="robot-operational-grid">
            <section className="dashboard-panel">
              <h2>
                {status?.online === false ? "Last known work" : "Current work"}
              </h2>
              {currentRuns?.length ? (
                currentRuns.map((run) => (
                  <p key={run.id}>
                    <a
                      className="text-action"
                      href={resourcePath("runs", run.id, robot.organization_id)}
                    >
                      {revisionNames[run.mission_revision_id ?? ""] ??
                        (run.mission_revision_id
                          ? "Unknown app"
                          : "Direct run")}{" "}
                      · {run.state} →
                    </a>
                  </p>
                ))
              ) : (
                <p>
                  {runs
                    ? "No active run reported."
                    : "Run information unavailable."}
                </p>
              )}
            </section>
            <section className="dashboard-panel records-detail">
              <h2>Deployment</h2>
              {deployment ? (
                <>
                  <dl className="record-fields">
                    <dt>Observed</dt>
                    <dd>{deployment.observed_state}</dd>
                    <dt>Desired</dt>
                    <dd>{deployment.desired_state}</dd>
                    <dt>App</dt>
                    <dd>
                      {deployment.selected_revision_id
                        ? (revisionNames[deployment.selected_revision_id] ??
                          "Unknown")
                        : "None"}
                    </dd>
                  </dl>
                  <details className="robot-technical">
                    <summary>Technical deployment details</summary>
                    <dl className="record-fields">
                      <dt>ID</dt>
                      <dd>{deployment.id}</dd> <dt>Selected revision</dt>
                      <dd>{deployment.selected_revision_id ?? "None"}</dd>
                      <dt>Candidate revision</dt>
                      <dd>{deployment.candidate_revision_id ?? "None"}</dd>
                      <dt>Last execution</dt>
                      <dd>
                        {deployment.last_execution_id ? (
                          <a
                            className="text-action"
                            href={resourcePath(
                              "runs",
                              deployment.last_execution_id,
                              robot.organization_id,
                            )}
                          >
                            Inspect last run
                          </a>
                        ) : (
                          "None"
                        )}
                      </dd>
                      <dt>Last operation</dt>
                      <dd>{deployment.last_operation_id ?? "Unavailable"}</dd>
                      <dt>Record updated</dt>
                      <dd>
                        {deployment.updated_at
                          ? new Date(deployment.updated_at).toLocaleString()
                          : "Unavailable"}
                      </dd>
                      <dt>Last error</dt>
                      <dd>
                        {deployment.last_error
                          ? `${deployment.last_error.code}: ${deployment.last_error.message}`
                          : "None"}
                      </dd>
                    </dl>
                  </details>
                  <div className="robot-actions">
                    <button
                      className="button-primary"
                      type="button"
                      disabled={
                        busy ||
                        role === "operator" ||
                        !deployment.selected_revision_id ||
                        !!deployment.candidate_revision_id
                      }
                      onClick={() => setReview("restart")}
                    >
                      Restart app
                    </button>
                    <button
                      className="button-secondary"
                      type="button"
                      disabled={busy || !deployment.selected_revision_id}
                      onClick={() => setReview("stop")}
                    >
                      Stop mission
                    </button>
                  </div>
                  {review && (
                    <div className="empty-panel">
                      <p>
                        {review === "restart"
                          ? "Stop the current execution and request a new run"
                          : "Request the current execution to stop"}{" "}
                        on {robot.name}?
                      </p>
                      <button
                        type="button"
                        className="button-primary"
                        onClick={() => void action(review)}
                      >
                        Confirm {review}
                      </button>{" "}
                      <button type="button" onClick={() => setReview(null)}>
                        Cancel
                      </button>
                    </div>
                  )}
                  <p>
                    Restart stops the current execution before starting a new
                    run.
                  </p>
                </>
              ) : (
                <p>No app deployed.</p>
              )}
            </section>
            <section className="dashboard-panel robot-resource-card">
              <h2>Resources</h2>
              <div className="robot-resource-item">
                <svg
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="1.8"
                  aria-hidden="true"
                >
                  <rect x="2" y="6" width="18" height="12" rx="2" />
                  <path d="M22 10v4" />
                </svg>
                <div>
                  <strong>Battery</strong>
                  <span>Unknown</span>
                </div>
              </div>
              <div className="robot-resource-item">
                <svg
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="1.8"
                  aria-hidden="true"
                >
                  <rect x="3" y="5" width="18" height="14" rx="2" />
                  <path d="M3 12h18" />
                </svg>
                <div>
                  <strong>Storage</strong>
                  <span>Unknown</span>
                </div>
              </div>
              <small>Readings have not been reported</small>
            </section>
          </div>
          <nav className="route-tabs" aria-label="Robot sections">
            {["Activity", "Configuration"].map((name) => (
              <button
                key={name}
                type="button"
                aria-current={tab === name ? "page" : undefined}
                onClick={() => setQuery("tab", name)}
              >
                {name}
              </button>
            ))}
          </nav>
          <div className="robot-detail-grid" hidden={tab !== "Configuration"}>
            <section
              hidden={tab !== "Configuration"}
              className="dashboard-panel records-detail"
            >
              <h2>Robot</h2>
              <dl className="record-fields">
                <dt>ID</dt>
                <dd>{robot.id}</dd>
                <dt>Organization</dt>
                <dd>{robot.organization_id}</dd>
                <dt>Created</dt>
                <dd>{robot.created_at}</dd>
                <dt>Enrolled</dt>
                <dd>{robot.enrolled_at ?? "Not enrolled"}</dd>
                <dt>Profile</dt>
                <dd>{robot.profile_ref ?? "Not set"}</dd>
              </dl>
            </section>
            <section className="dashboard-panel records-detail">
              <h2>Daemon</h2>
              {status ? (
                <dl className="record-fields">
                  <dt>Version</dt>
                  <dd>{status.daemon_version ?? "Unknown"}</dd>
                  <dt>Protocol</dt>
                  <dd>{status.protocol_version ?? "Unknown"}</dd>
                </dl>
              ) : (
                <p>Loading status…</p>
              )}
            </section>
          </div>
          {tab !== "Configuration" && (
            <a
              className="text-action robot-timeline-link"
              href={
                resourcePath("timeline", "", robot.organization_id) +
                "&robot=" +
                encodeURIComponent(robot.id)
              }
            >
              View robot timeline →
            </a>
          )}
          <section
            hidden={tab === "Configuration"}
            className="dashboard-panel records-detail"
          >
            <h2>Runs</h2>
            {runs === null ? (
              <p>Loading runs…</p>
            ) : runs.length === 0 ? (
              <p>No runs yet.</p>
            ) : (
              <div className="table-scroll">
                <table className="dashboard-table">
                  <thead>
                    <tr>
                      <th>Execution</th>
                      <th>State</th>
                      <th>Created</th>
                      <th>Exit code</th>
                    </tr>
                  </thead>
                  <tbody>
                    {runs.map((item) => (
                      <tr key={item.id}>
                        <td>
                          <a
                            className="text-action"
                            href={resourcePath(
                              "runs",
                              item.id,
                              robot.organization_id,
                            )}
                          >
                            {revisionNames[item.mission_revision_id ?? ""] ??
                              (item.mission_revision_id
                                ? "Unknown app"
                                : "Direct run")}
                          </a>
                        </td>
                        <td>
                          <span
                            className="logbook2-state"
                            data-state={item.state}
                          >
                            {item.state}
                          </span>
                        </td>
                        <td>
                          <StudioTime value={item.created_at} />
                        </td>
                        <td>{item.exit_code ?? "Unavailable"}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        </>
      )}
    </main>
  );
}
