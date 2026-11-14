import { useAuth0 } from "@auth0/auth0-react";
import { unzipSync } from "fflate";
import { useCallback, useEffect, useState } from "react";
import { MissionControls } from "@/components/MissionControls";
import { apiFetch, apiFetchAll } from "@/lib/api";

type Organization = { id: string; name: string };
type Robot = { id: string; name: string };
type Mission = {
  id: string;
  name: string;
  latest_version: number;
  created_at: string;
  created_by_user_id: string;
};
type Revision = {
  id: string;
  version: number;
  created_at: string;
  script_revision_id: string;
  manifest: {
    entrypoint: string;
    python_requirement: string;
    source_digest: string;
    lock_file: string;
    robot_profile_ref?: string;
  };
};
type Deployment = {
  id: string;
  robot_id: string;
  selected_revision_id: string | null;
  candidate_revision_id: string | null;
  desired_state: string;
  observed_state: string;
  last_execution_id: string | null;
  last_operation_id: string | null;
  last_error: { code: string; message: string } | null;
  created_at: string;
  updated_at: string;
};
const message = (error: unknown) =>
  error instanceof Error ? error.message : "Request failed";

export function RecordsPage({
  kind,
  preferredOrganizationId,
  onOrganizationChange,
}: {
  kind: "missions" | "deployments";
  preferredOrganizationId: string;
  onOrganizationChange: (id: string) => void;
}) {
  const { getAccessTokenSilently } = useAuth0();
  const getToken = useCallback(async (): Promise<string> => {
    const token = await getAccessTokenSilently({
      authorizationParams: { audience: import.meta.env.VITE_AUTH0_AUDIENCE },
    });
    if (!token) throw new Error("No access token available");
    return token;
  }, [getAccessTokenSilently]);
  const [orgs, setOrgs] = useState<Organization[] | null>(null);
  const [orgId, setOrgId] = useState(preferredOrganizationId);
  const [missions, setMissions] = useState<Mission[] | null>(null);
  const [deployments, setDeployments] = useState<Deployment[] | null>(null);
  const [robots, setRobots] = useState<Robot[]>([]);
  const [selected, setSelected] = useState("");
  const [revisions, setRevisions] = useState<Revision[] | null>(null);
  const [revisionId, setRevisionId] = useState("");
  const [files, setFiles] = useState<Record<string, Uint8Array> | null>(null);
  const [fileName, setFileName] = useState("");
  const [error, setError] = useState("");
  const [detailError, setDetailError] = useState("");
  const [refresh, setRefresh] = useState(0);

  useEffect(() => {
    let cancelled = false;
    apiFetchAll<Organization>(getToken, "/v1/organizations")
      .then((items) => {
        if (cancelled) return;
        setOrgs(items);
        setOrgId((current) =>
          items.some((item) => item.id === current)
            ? current
            : (items[0]?.id ?? ""),
        );
      })
      .catch((e) => {
        if (!cancelled) setError(message(e));
      });
    return () => {
      cancelled = true;
    };
  }, [getToken]);
  // The counter intentionally re-runs the snapshot request.
  // biome-ignore lint/correctness/useExhaustiveDependencies: refresh requests a fresh snapshot
  useEffect(() => {
    if (!orgId) return;
    let cancelled = false;
    setError("");
    setMissions(null);
    setDeployments(null);
    setRobots([]);
    const path = `/v1/organizations/${orgId}`;
    Promise.all([
      apiFetchAll<Mission>(getToken, `${path}/missions`),
      apiFetchAll<Robot>(getToken, `${path}/robots`),
      ...(kind === "deployments"
        ? [apiFetchAll<Deployment>(getToken, `${path}/deployments`)]
        : []),
    ])
      .then(([m, r, d]) => {
        if (!cancelled) {
          setMissions(m as Mission[]);
          setRobots(r as Robot[]);
          if (kind === "deployments") setDeployments(d as Deployment[]);
        }
      })
      .catch((e) => {
        if (!cancelled) setError(message(e));
      });
    return () => {
      cancelled = true;
    };
  }, [orgId, kind, getToken, refresh]);
  useEffect(() => {
    setRevisions(null);
    setRevisionId("");
    setDetailError("");
    if (kind !== "missions" || !selected) return;
    let cancelled = false;
    apiFetchAll<Revision>(getToken, `/v1/missions/${selected}/revisions`)
      .then((items) => {
        if (!cancelled) {
          setRevisions(items);
          setRevisionId(items.at(-1)?.id ?? "");
        }
      })
      .catch((e) => {
        if (!cancelled) setDetailError(message(e));
      });
    return () => {
      cancelled = true;
    };
  }, [selected, kind, getToken]);
  useEffect(() => {
    setFiles(null);
    setFileName("");
    if (!selected || !revisionId || kind !== "missions") return;
    let cancelled = false;
    apiFetch(
      getToken,
      `/v1/missions/${selected}/revisions/${revisionId}/source`,
    )
      .then((r) => r.arrayBuffer())
      .then((data) => {
        const entries = unzipSync(new Uint8Array(data));
        if (!cancelled) {
          setFiles(entries);
          const revision = revisions?.find((item) => item.id === revisionId);
          setFileName(
            revision?.manifest.entrypoint &&
              entries[revision.manifest.entrypoint]
              ? revision.manifest.entrypoint
              : (Object.keys(entries)[0] ?? ""),
          );
        }
      })
      .catch((e) => {
        if (!cancelled) setDetailError(message(e));
      });
    return () => {
      cancelled = true;
    };
  }, [selected, revisionId, kind, getToken, revisions]);
  const mission = missions?.find((item) => item.id === selected);
  const revision = revisions?.find((item) => item.id === revisionId);
  const deployment = deployments?.find((item) => item.id === selected);
  const file = files?.[fileName];
  let code = "";
  if (file) {
    try {
      code = new TextDecoder("utf-8", { fatal: true }).decode(file);
      if (code.includes("\0")) code = "Binary file";
    } catch {
      code = "Binary file";
    }
  }
  return (
    <div className="resource-layout">
      <aside
        className="resource-sidebar"
        aria-label={kind === "missions" ? "Mission list" : "Deployment list"}
      >
        <div className="resource-sidebar-header">
          <h1>{kind === "missions" ? "Missions" : "Deployments"}</h1>
          <label className="resource-workspace">
            Workspace
            <select
              value={orgId}
              onChange={(event) => {
                setSelected("");
                setOrgId(event.target.value);
                onOrganizationChange(event.target.value);
              }}
            >
              {orgs?.map((org) => (
                <option key={org.id} value={org.id}>
                  {org.name}
                </option>
              ))}
            </select>
          </label>
          <button
            type="button"
            className="button-secondary"
            onClick={() => setRefresh((value) => value + 1)}
          >
            Refresh
          </button>
        </div>
        {error && (
          <p className="notice-error" role="alert">
            {error}
          </p>
        )}
        {orgs?.length === 0 && <p>No workspace available.</p>}
        <div className="resource-list">
          {kind === "missions" ? (
            missions === null ? (
              <p>Loading missions…</p>
            ) : missions.length === 0 ? (
              <p>No missions published.</p>
            ) : (
              missions.map((item) => (
                <button
                  type="button"
                  className="record-row"
                  aria-label={`${item.name} ${item.latest_version ? `version ${item.latest_version}` : "no revisions"}`}
                  aria-pressed={selected === item.id}
                  onClick={() => setSelected(item.id)}
                  key={item.id}
                >
                  <strong>{item.name}</strong>
                  <span>
                    {item.latest_version
                      ? `Version ${item.latest_version}`
                      : "No revisions"}
                  </span>
                </button>
              ))
            )
          ) : deployments === null ? (
            <p>Loading deployments…</p>
          ) : deployments.length === 0 ? (
            <p>No deployments.</p>
          ) : (
            deployments.map((item) => (
              <button
                type="button"
                className="record-row"
                aria-label={`${robots.find((robot) => robot.id === item.robot_id)?.name ?? item.robot_id} ${item.observed_state}`}
                aria-pressed={selected === item.id}
                onClick={() => setSelected(item.id)}
                key={item.id}
              >
                <strong>
                  {robots.find((robot) => robot.id === item.robot_id)?.name ??
                    item.robot_id}
                </strong>
                <span>{item.observed_state}</span>
              </button>
            ))
          )}
        </div>
      </aside>
      <main className="resource-content">
        <section className="records-detail resource-detail">
          {!selected && (
            <p>
              Select {kind === "missions" ? "a mission" : "a deployment"} to
              inspect it.
            </p>
          )}
          {detailError && (
            <p role="alert" className="notice-error">
              {detailError}
            </p>
          )}
          {mission && (
            <div className="mission-detail">
              <header className="mission-header">
                <div>
                  <h1>{mission.name}</h1>
                  <p>
                    Created{" "}
                    {new Date(mission.created_at).toLocaleDateString(
                      undefined,
                      { year: "numeric", month: "short", day: "numeric" },
                    )}
                  </p>
                </div>
                <div className="mission-version-select">
                  {revisions === null ? (
                    <span>Loading revisions…</span>
                  ) : revisions.length === 0 ? (
                    <span>No revisions published</span>
                  ) : (
                    <label>
                      Version
                      <select
                        value={revisionId}
                        onChange={(event) => {
                          setDetailError("");
                          setRevisionId(event.target.value);
                        }}
                      >
                        {revisions.map((item) => (
                          <option value={item.id} key={item.id}>
                            {item.version}
                          </option>
                        ))}
                      </select>
                    </label>
                  )}
                </div>
              </header>
              {revision && (
                <div className="mission-primary-grid">
                  <section className="mission-panel mission-source">
                    <div className="mission-panel-header">
                      <div>
                        <h2>Source files</h2>
                        <p>Revision {revision.version}</p>
                      </div>
                      {files && (
                        <label>
                          File{" "}
                          <select
                            value={fileName}
                            onChange={(event) =>
                              setFileName(event.target.value)
                            }
                          >
                            {Object.keys(files)
                              .filter((name) => !name.endsWith("/"))
                              .sort()
                              .map((name) => (
                                <option key={name}>{name}</option>
                              ))}
                          </select>
                        </label>
                      )}
                    </div>
                    {files === null ? (
                      <p className="mission-source-message">
                        {detailError
                          ? "Source unavailable."
                          : "Loading source…"}
                      </p>
                    ) : (
                      <pre className="source-code">
                        <code>{code}</code>
                      </pre>
                    )}
                  </section>
                  <section className="mission-panel mission-run-panel">
                    <MissionControls
                      organizationId={orgId}
                      revisionId={revision.id}
                      robots={robots}
                      getToken={getToken}
                    />
                  </section>
                </div>
              )}
              <div className="mission-metadata-grid">
                <section className="mission-panel mission-metadata">
                  <h2>Mission details</h2>
                  <dl className="record-fields">
                    <dt>Mission ID</dt>
                    <dd>{mission.id}</dd>
                    <dt>Created</dt>
                    <dd>{mission.created_at}</dd>
                    <dt>Created by</dt>
                    <dd>{mission.created_by_user_id}</dd>
                  </dl>
                </section>
                {revision && (
                  <section className="mission-panel mission-metadata">
                    <h2>Revision details</h2>
                    <dl className="record-fields">
                      <dt>Revision ID</dt>
                      <dd>{revision.id}</dd>
                      <dt>Published</dt>
                      <dd>{revision.created_at}</dd>
                      <dt>Entrypoint</dt>
                      <dd>{revision.manifest.entrypoint}</dd>
                      <dt>Python</dt>
                      <dd>{revision.manifest.python_requirement}</dd>
                      <dt>Lock file</dt>
                      <dd>{revision.manifest.lock_file}</dd>
                      <dt>Source digest</dt>
                      <dd>{revision.manifest.source_digest}</dd>
                      {revision.manifest.robot_profile_ref && (
                        <>
                          <dt>Robot profile</dt>
                          <dd>{revision.manifest.robot_profile_ref}</dd>
                        </>
                      )}
                    </dl>
                  </section>
                )}
              </div>
            </div>
          )}
          {deployment && (
            <>
              <h1>Deployment</h1>
              <dl className="record-fields">
                <dt>Deployment ID</dt>
                <dd>{deployment.id}</dd>
                <dt>Robot</dt>
                <dd>
                  {robots.find((robot) => robot.id === deployment.robot_id)
                    ?.name ?? deployment.robot_id}
                </dd>
                <dt>Robot ID</dt>
                <dd>{deployment.robot_id}</dd>
                <dt>Observed state</dt>
                <dd>{deployment.observed_state}</dd>
                <dt>Desired state</dt>
                <dd>{deployment.desired_state}</dd>
                <dt>Selected revision</dt>
                <dd>{deployment.selected_revision_id ?? "None"}</dd>
                <dt>Candidate revision</dt>
                <dd>{deployment.candidate_revision_id ?? "None"}</dd>
                <dt>Last operation</dt>
                <dd>{deployment.last_operation_id ?? "None"}</dd>
                <dt>Last execution</dt>
                <dd>{deployment.last_execution_id ?? "None"}</dd>
                <dt>Last error</dt>
                <dd>
                  {deployment.last_error
                    ? `${deployment.last_error.code}: ${deployment.last_error.message}`
                    : "None"}
                </dd>
                <dt>Created</dt>
                <dd>{deployment.created_at}</dd>
                <dt>Updated</dt>
                <dd>{deployment.updated_at}</dd>
              </dl>
            </>
          )}
        </section>
      </main>
    </div>
  );
}
