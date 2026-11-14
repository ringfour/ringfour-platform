import { useAuth0 } from "@auth0/auth0-react";
import { useCallback, useEffect, useState } from "react";
import { apiFetch, apiFetchAll } from "@/lib/api";

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
type Output = { data_base64: string; next_offset: number; truncated: boolean };
const describe = (error: unknown) =>
  error instanceof Error ? error.message : "Request failed";

export function RobotDetailPage({
  robotId,
  onBack,
  onManage,
}: {
  robotId: string;
  onBack: () => void;
  onManage: () => void;
}) {
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
  const [run, setRun] = useState<Execution | null>(null);
  const [output, setOutput] = useState<{
    stdout: string;
    stderr: string;
    truncated: boolean;
  } | null>(null);
  const [error, setError] = useState("");
  const [outputError, setOutputError] = useState("");
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
  useEffect(() => {
    let cancelled = false;
    setOutput(null);
    setOutputError("");
    if (!run) return;
    async function read(stream: string) {
      let offset = 0;
      let truncated = false;
      const decoder = new TextDecoder();
      let text = "";
      do {
        const chunk = (await apiFetch(
          getToken,
          `/v1/executions/${run?.id}/output?stream=${stream}&offset=${offset}&limit=16384`,
        ).then((r) => r.json())) as Output;
        const binary = atob(chunk.data_base64);
        const bytes = Uint8Array.from(binary, (char) => char.charCodeAt(0));
        text += decoder.decode(bytes, { stream: true });
        truncated ||= chunk.truncated;
        if (chunk.next_offset <= offset || chunk.next_offset - offset < 16384)
          break;
        offset = chunk.next_offset;
      } while (offset < 65536);
      return { text: text + decoder.decode(), truncated };
    }
    Promise.all([read("stdout"), read("stderr")])
      .then(([out, err]) => {
        if (!cancelled)
          setOutput({
            stdout: out.text,
            stderr: err.text,
            truncated: out.truncated || err.truncated,
          });
      })
      .catch((e) => {
        if (!cancelled) setOutputError(describe(e));
      });
    return () => {
      cancelled = true;
    };
  }, [run, getToken]);
  async function action(kind: "restart" | "stop") {
    if (!deployment || busy) return;
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
        Back to fleet
      </button>
      <div className="dashboard-heading">
        <div>
          <h1>{robot?.name ?? "Robot"}</h1>
          <p>Robot details, deployments and runs.</p>
        </div>
        <div className="robot-actions">
          <button className="button-secondary" type="button" onClick={onManage}>
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
          <div className="robot-detail-grid">
            <section className="dashboard-panel records-detail">
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
                  <dt>Connection</dt>
                  <dd>{status.online ? "Online" : "Offline"}</dd>
                  <dt>Last seen</dt>
                  <dd>{status.last_seen_at ?? "Never"}</dd>
                  <dt>Version</dt>
                  <dd>{status.daemon_version ?? "Unknown"}</dd>
                  <dt>Protocol</dt>
                  <dd>{status.protocol_version ?? "Unknown"}</dd>
                </dl>
              ) : (
                <p>Loading status…</p>
              )}
              <p>Reported through the Platform connection.</p>
            </section>
          </div>
          <section className="dashboard-panel records-detail">
            <h2>Deployment</h2>
            {deployment ? (
              <>
                <dl className="record-fields">
                  <dt>ID</dt>
                  <dd>{deployment.id}</dd>
                  <dt>Observed</dt>
                  <dd>{deployment.observed_state}</dd>
                  <dt>Desired</dt>
                  <dd>{deployment.desired_state}</dd>
                  <dt>Mission</dt>
                  <dd>
                    {deployment.selected_revision_id
                      ? (revisionNames[deployment.selected_revision_id] ??
                        "Unknown")
                      : "None"}
                  </dd>
                  <dt>Selected revision</dt>
                  <dd>{deployment.selected_revision_id ?? "None"}</dd>
                  <dt>Candidate revision</dt>
                  <dd>{deployment.candidate_revision_id ?? "None"}</dd>
                  <dt>Last execution</dt>
                  <dd>{deployment.last_execution_id ?? "None"}</dd>
                  <dt>Last error</dt>
                  <dd>
                    {deployment.last_error
                      ? `${deployment.last_error.code}: ${deployment.last_error.message}`
                      : "None"}
                  </dd>
                </dl>
                <div className="robot-actions">
                  <button
                    className="button-primary"
                    type="button"
                    disabled={
                      busy ||
                      !deployment.selected_revision_id ||
                      !!deployment.candidate_revision_id
                    }
                    onClick={() => void action("restart")}
                  >
                    Restart mission
                  </button>
                  <button
                    className="button-secondary"
                    type="button"
                    disabled={busy || !deployment.selected_revision_id}
                    onClick={() => void action("stop")}
                  >
                    Stop mission
                  </button>
                </div>
                <p>
                  Restart stops the current execution before starting a new run.
                </p>
              </>
            ) : (
              <p>No mission deployed.</p>
            )}
          </section>
          <section className="dashboard-panel records-detail">
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
                          <button
                            type="button"
                            className="text-action"
                            onClick={() => setRun(item)}
                          >
                            {item.id}
                          </button>
                        </td>
                        <td>{item.state}</td>
                        <td>{item.created_at}</td>
                        <td>{item.exit_code ?? "Unavailable"}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            {run && (
              <div>
                <h3>Execution {run.id}</h3>
                <dl className="record-fields">
                  <dt>Deployment</dt>
                  <dd>{run.deployment_id ?? "Script run"}</dd>
                  <dt>Mission revision</dt>
                  <dd>
                    {run.mission_revision_id
                      ? `${revisionNames[run.mission_revision_id] ?? "Unknown"} (${run.mission_revision_id})`
                      : "None"}
                  </dd>
                  <dt>Updated</dt>
                  <dd>{run.updated_at}</dd>
                  <dt>Error</dt>
                  <dd>
                    {run.error
                      ? `${run.error.code}: ${run.error.message}`
                      : "None"}
                  </dd>
                </dl>
                {outputError ? (
                  <p role="alert">{outputError}</p>
                ) : output === null ? (
                  <p>Loading output…</p>
                ) : (
                  <>
                    {output.truncated && <p>Stored output was truncated.</p>}
                    <h3>Standard output</h3>
                    <pre className="source-code">
                      {output.stdout || "No output"}
                    </pre>
                    <h3>Standard error</h3>
                    <pre className="source-code">
                      {output.stderr || "No output"}
                    </pre>
                  </>
                )}
              </div>
            )}
          </section>
        </>
      )}
    </main>
  );
}
