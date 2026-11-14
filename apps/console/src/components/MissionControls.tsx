import { useCallback, useEffect, useState } from "react";
import { apiFetch, apiFetchAll } from "@/lib/api";

type Robot = { id: string; name: string };
type Deployment = {
  id: string;
  robot_id: string;
  selected_revision_id: string | null;
  candidate_revision_id: string | null;
  observed_state: string;
  last_error: { code: string; message: string } | null;
};

export function MissionControls({
  organizationId,
  revisionId,
  robots,
  getToken,
}: {
  organizationId: string;
  revisionId: string;
  robots: Robot[];
  getToken: () => Promise<string>;
}) {
  const [robotId, setRobotId] = useState("");
  const [deployments, setDeployments] = useState<Deployment[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const load = useCallback(async () => {
    const items = await apiFetchAll<Deployment>(
      getToken,
      `/v1/organizations/${organizationId}/deployments`,
    );
    setDeployments(items);
  }, [getToken, organizationId]);
  useEffect(() => {
    let active = true;
    const update = () => {
      void load().catch((e) => {
        if (active)
          setError(
            e instanceof Error ? e.message : "Could not load deployments",
          );
      });
    };
    update();
    const timer = setInterval(update, 5000);
    return () => {
      active = false;
      clearInterval(timer);
    };
  }, [load]);
  const deployment = deployments?.find((item) => item.robot_id === robotId);
  const ready =
    deployment?.selected_revision_id === revisionId &&
    !deployment.candidate_revision_id;
  const pending =
    !!deployment?.candidate_revision_id ||
    deployment?.observed_state === "starting" ||
    deployment?.observed_state === "stopping";
  const active = deployment?.observed_state === "running";
  async function submit(action: "prepare" | "start") {
    if (!robotId || busy) return;
    setBusy(true);
    setError("");
    setNotice("");
    try {
      const path =
        action === "prepare"
          ? `/v1/robots/${robotId}/deployments`
          : `/v1/deployments/${deployment?.id}/start`;
      const response = await apiFetch(getToken, path, {
        method: "POST",
        headers: {
          "Idempotency-Key": crypto.randomUUID(),
          ...(action === "prepare"
            ? { "Content-Type": "application/json" }
            : {}),
        },
        ...(action === "prepare"
          ? { body: JSON.stringify({ mission_revision_id: revisionId }) }
          : {}),
      });
      const result = (await response.json()) as {
        operation_id: string;
        execution_id?: string | null;
      };
      setNotice(
        `${action === "prepare" ? "Preparation" : "Start"} accepted. Operation ${result.operation_id}${result.execution_id ? `; execution ${result.execution_id}` : ""}.`,
      );
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Request failed");
    } finally {
      setBusy(false);
    }
  }
  return (
    <section className="mission-controls">
      <h3>Run on a robot</h3>
      <label>
        Robot{" "}
        <select
          value={robotId}
          onChange={(event) => {
            setRobotId(event.target.value);
            setError("");
            setNotice("");
          }}
        >
          <option value="">Choose a robot</option>
          {robots.map((robot) => (
            <option key={robot.id} value={robot.id}>
              {robot.name}
            </option>
          ))}
        </select>
      </label>
      {robotId && deployments === null && <p>Loading deployment…</p>}
      {robotId && deployments && (
        <>
          <p>
            {deployment
              ? `Deployment state: ${deployment.observed_state}`
              : "No mission prepared on this robot."}
          </p>
          {deployment?.last_error && (
            <p role="alert">
              {deployment.last_error.code}: {deployment.last_error.message}
            </p>
          )}
          {!ready && (
            <button
              className="button-secondary"
              type="button"
              disabled={busy || pending || active}
              onClick={() => void submit("prepare")}
            >
              Prepare this revision
            </button>
          )}
          {ready && (
            <button
              className="button-primary"
              type="button"
              disabled={busy || pending || active}
              onClick={() => void submit("start")}
            >
              Start mission
            </button>
          )}
          {active && (
            <p>
              A mission is running on this robot. Stop it before starting
              another.
            </p>
          )}
        </>
      )}
      {notice && <p role="status">{notice}</p>}
      {error && (
        <p role="alert" className="notice-error">
          {error}
        </p>
      )}
    </section>
  );
}
