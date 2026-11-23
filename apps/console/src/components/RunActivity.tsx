import { StudioTime } from "./StudioTime";
import { useEffect, useState } from "react";
import { apiFetchAll } from "@/lib/api";
import { resourcePath } from "@/lib/location";

type Run = {
  id: string;
  mission_revision_id: string | null;
  state: string;
  updated_at: string;
  actor_user_id: string | null;
};
type Deployment = {
  id: string;
  robot_id: string;
  selected_revision_id: string | null;
  desired_state: string;
  observed_state: string;
  last_error: { message: string } | null;
};
type Named = { id: string; name: string };
type Revision = { id: string; version: number };
type Row = Run & { robot: Named; app?: Named; version?: number };

export function RunActivity({
  workspace,
  apps,
  robots,
  getToken,
  appId,
  refresh,
}: {
  workspace: string;
  apps: Named[];
  robots: Named[];
  getToken: () => Promise<string>;
  appId?: string;
  refresh: number;
}) {
  const [names, setNames] = useState(new Map<string, string>());
  const [deployments, setDeployments] = useState<Deployment[] | null>(null);
  const [rows, setRows] = useState<Row[] | null>(null);
  const [error, setError] = useState("");
  // biome-ignore lint/correctness/useExhaustiveDependencies: Refresh requests an updated workspace snapshot.
  useEffect(() => {
    let current = true;
    setRows(null);
    setDeployments(null);
    setError("");
    async function load() {
      void apiFetchAll<{ user_id: string; name: string }>(
        getToken,
        "/v1/organizations/" + workspace + "/members",
      )
        .then((members) => {
          if (current)
            setNames(new Map(members.map((m) => [m.user_id, m.name])));
        })
        .catch(() => {});
      const revisions = new Map<string, { app: Named; version: number }>();
      for (const app of apps) {
        if (appId && app.id !== appId) continue;
        for (const revision of await apiFetchAll<Revision>(
          getToken,
          `/v1/missions/${app.id}/revisions`,
        ))
          revisions.set(revision.id, { app, version: revision.version });
      }
      void apiFetchAll<Deployment>(
        getToken,
        `/v1/organizations/${workspace}/deployments`,
      )
        .then((items) => {
          if (current)
            setDeployments(
              items.filter(
                (item) =>
                  !appId || revisions.has(item.selected_revision_id ?? ""),
              ),
            );
        })
        .catch(() => {});
      const result: Row[] = [];
      for (const robot of robots) {
        for (const run of await apiFetchAll<Run>(
          getToken,
          `/v1/robots/${robot.id}/executions`,
        )) {
          const revision = revisions.get(run.mission_revision_id ?? "");
          if (!appId || revision?.app.id === appId)
            result.push({ ...run, robot, ...revision });
        }
      }
      if (current)
        setRows(
          result.sort((a, b) => b.updated_at.localeCompare(a.updated_at)),
        );
    }
    void load().catch(() => {
      if (current) setError("Run activity is unavailable.");
    });
    return () => {
      current = false;
    };
  }, [workspace, apps, robots, getToken, appId, refresh]);
  if (error)
    return (
      <p role="alert" className="notice-error">
        {error}
      </p>
    );
  if (!rows) return <p className="subtle">Loading run activity…</p>;
  const issues = deployments?.filter(
    (item) =>
      item.last_error ||
      (item.desired_state === "running" && item.observed_state !== "running") ||
      (item.desired_state === "stopped" && item.observed_state === "running"),
  );
  const failed = rows.filter((row) => row.state === "failed").slice(0, 3);
  return (
    <div className="activity-sections">
      {appId || issues?.length || failed.length ? (
        <section className="dashboard-panel">
          <header className="panel-header">
            <h2>{appId ? "Deployments" : "Needs attention"}</h2>
          </header>
          <ul className="activity-list">
            {(appId ? deployments : issues)?.map((item) => (
              <li key={item.id}>
                <a
                  className="text-action"
                  href={
                    resourcePath("robots", item.robot_id, workspace) +
                    "&tab=Deployment"
                  }
                >
                  {robots.find((robot) => robot.id === item.robot_id)?.name ??
                    item.robot_id}
                </a>
                <div>
                  Requested {item.desired_state} · Observed{" "}
                  {item.observed_state}
                  {item.last_error && <p>{item.last_error.message}</p>}
                </div>
              </li>
            ))}
            {!appId &&
              failed.map((row) => (
                <li key={row.id}>
                  <a
                    className="text-action"
                    href={resourcePath("runs", row.id, workspace)}
                  >
                    Inspect failed run · {row.app?.name ?? "Direct run"}
                  </a>
                  <span>{row.robot.name}</span>
                </li>
              ))}
          </ul>
          {appId && (
            <p className="subtle">
              {deployments === null
                ? "Deployment information unavailable"
                : deployments.length === 0
                  ? "No deployments for this app."
                  : ""}
            </p>
          )}
        </section>
      ) : null}
      {["Happening now", "Recent outcomes"].map((heading, index) => {
        const list = rows
          .filter(
            (row) =>
              ["succeeded", "failed", "cancelled"].includes(row.state) ===
              Boolean(index),
          )
          .slice(0, 5);
        const group = index ? "Finished" : "Active";
        return (
          <section className="dashboard-panel" key={heading}>
            <header className="panel-header">
              <h2>{heading}</h2>
              <a
                className="text-action"
                href={
                  resourcePath("runs", "", workspace) +
                  "&group=" +
                  group +
                  (appId ? "&app=" + encodeURIComponent(appId) : "")
                }
              >
                View runs →
              </a>
            </header>
            {list.length === 0 ? (
              <p className="table-empty">
                {index ? "No finished runs." : "No active runs."}
              </p>
            ) : (
              <ul className="activity-list">
                {list.map((row) => (
                  <li key={row.id}>
                    <div>
                      <a
                        className="text-action"
                        href={resourcePath("runs", row.id, workspace)}
                      >
                        {row.app?.name ??
                          (row.mission_revision_id
                            ? "Unknown app"
                            : "Direct run")}
                        {row.version !== undefined && " v" + row.version}
                      </a>
                      <p>
                        <a
                          href={resourcePath("robots", row.robot.id, workspace)}
                        >
                          {row.robot.name}
                        </a>{" "}
                        · Started by{" "}
                        {names.get(row.actor_user_id ?? "") ??
                          "name unavailable"}
                      </p>
                    </div>
                    <div>
                      <span className="logbook2-state" data-state={row.state}>
                        {row.state === "succeeded"
                          ? "Execution succeeded"
                          : row.state}
                      </span>
                      <p>
                        <StudioTime value={row.updated_at} />
                      </p>
                    </div>
                  </li>
                ))}
              </ul>
            )}
          </section>
        );
      })}
    </div>
  );
}
