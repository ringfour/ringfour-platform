import { useAuth0 } from "@auth0/auth0-react";
import { useCallback, useEffect, useState } from "react";
import { AppOperationStatus } from "@/components/AppOperationStatus";
import { AppStorePage } from "@/components/AppStorePage";
import {
  type App,
  type AppConfiguration,
  type AppImport,
  type AppRun,
  type Installation,
  mutateApp,
  type Revision,
  type Robot,
  readJson,
  readPage,
  type Token,
} from "@/lib/apps";
import "./app-workflows.css";

const message = (value: unknown) =>
  value instanceof Error ? value.message : "Request failed.";

export function AppsPage({
  organizationId,
  workspaceRole,
  focusAppId,
}: {
  organizationId: string;
  workspaceRole?: string;
  focusAppId?: string;
}) {
  const { getAccessTokenSilently } = useAuth0();
  const token = useCallback<Token>(async () => {
    const value = await getAccessTokenSilently({
      authorizationParams: { audience: import.meta.env.VITE_AUTH0_AUDIENCE },
    });
    if (!value) throw new Error("Sign in required");
    return value;
  }, [getAccessTokenSilently]);
  const [imports, setImports] = useState<AppImport[]>([]);
  const [apps, setApps] = useState<Record<string, App>>({});
  const [installations, setInstallations] = useState<Installation[]>([]);
  const [robots, setRobots] = useState<Robot[]>([]);
  const [runs, setRuns] = useState<AppRun[]>([]);
  const [revisionChoices, setRevisionChoices] = useState<
    Record<string, Revision[]>
  >({});
  const [configurationValues, setConfigurationValues] = useState<
    Record<string, AppConfiguration["values"]>
  >({});
  const [editing, setEditing] = useState<{
    installation: Installation;
    revision: Revision;
    values: Record<string, string | number | boolean>;
  } | null>(null);
  const [operation, setOperation] = useState("");
  const [error, setError] = useState("");
  const [refresh, setRefresh] = useState(0);
  const canManage = workspaceRole === "owner" || workspaceRole === "developer";
  const canOperate = canManage || workspaceRole === "operator";
  // biome-ignore lint/correctness/useExhaustiveDependencies: refresh explicitly requests a new snapshot
  useEffect(() => {
    let live = true;
    async function load() {
      try {
        const [importRows, robotRows, runRows] = await Promise.all([
          readPage<AppImport>(
            token,
            `/v1/organizations/${organizationId}/app-imports`,
          ),
          readPage<Robot>(token, `/v1/organizations/${organizationId}/robots`),
          readPage<AppRun>(
            token,
            `/v1/organizations/${organizationId}/app-runs`,
          ),
        ]);
        const installationGroups = await Promise.all(
          robotRows.map((robot) =>
            readPage<Installation>(
              token,
              `/v1/robots/${robot.id}/app-installations`,
            ),
          ),
        );
        const allInstallations = installationGroups.flat();
        const appIds = [
          ...new Set([
            ...importRows.map((x) => x.app_id),
            ...allInstallations.map((x) => x.app_id),
          ]),
        ];
        const [appPairs, revisionPairs, configurationPairs] = await Promise.all(
          [
            Promise.all(
              appIds.map(
                async (id) =>
                  [id, await readJson<App>(token, `/v1/apps/${id}`)] as const,
              ),
            ),
            Promise.all(
              appIds.map(
                async (id) =>
                  [
                    id,
                    await readPage<Revision>(
                      token,
                      `/v1/apps/${id}/revisions`,
                    ).catch(() => []),
                  ] as const,
              ),
            ),
            Promise.all(
              allInstallations
                .filter((item) => item.installed)
                .map(
                  async (item) =>
                    [
                      item.id,
                      await readJson<AppConfiguration>(
                        token,
                        `/v1/app-installations/${item.id}/configuration-revisions/${item.installed!.configuration_revision_id}`,
                      )
                        .then((value) => value.values)
                        .catch(() => ({})),
                    ] as const,
                ),
            ),
          ],
        );
        if (!live) return;
        setImports(importRows);
        setRobots(robotRows);
        setRuns(runRows);
        setApps(Object.fromEntries(appPairs));
        setInstallations(allInstallations);
        setRevisionChoices(Object.fromEntries(revisionPairs));
        setConfigurationValues(Object.fromEntries(configurationPairs));
        setError("");
      } catch (cause) {
        if (live) setError(message(cause));
      }
    }
    void load();
    return () => {
      live = false;
    };
  }, [token, organizationId, refresh]);

  async function loadChoices(appId: string) {
    if (revisionChoices[appId]) return revisionChoices[appId];
    const rows = await readPage<Revision>(token, `/v1/apps/${appId}/revisions`);
    setRevisionChoices((old) => ({ ...old, [appId]: rows }));
    return rows;
  }
  async function update(
    installation: Installation,
    kind: "update" | "start" | "stop",
  ) {
    setError("");
    try {
      const candidate = installation.installed;
      const revisions =
        kind === "update" ? await loadChoices(installation.app_id) : [];
      const next =
        kind === "update"
          ? revisions
              .filter((x) => x.id !== candidate?.revision_id)
              .sort((a, b) => b.published_at.localeCompare(a.published_at))[0]
          : undefined;
      if (kind === "update" && !next)
        throw new Error("No newer revision is available.");
      const body =
        kind === "start"
          ? {
              expected_revision_id: candidate?.revision_id,
              expected_configuration_revision_id:
                candidate?.configuration_revision_id,
            }
          : kind === "stop"
            ? {
                expected_run_id: runs.find(
                  (r) =>
                    r.installation_id === installation.id &&
                    ["accepted", "running"].includes(r.state),
                )?.id,
              }
            : {
                expected_revision_id: candidate?.revision_id,
                expected_configuration_revision_id:
                  candidate?.configuration_revision_id,
                revision_id: next?.id,
                share_id: null,
                configuration: next?.manifest.configuration.defaults,
              };
      if (kind === "stop" && !body.expected_run_id)
        throw new Error("There is no active run to stop.");
      const result = await mutateApp<{ operation_id: string }>(
        token,
        `/v1/app-installations/${installation.id}/${kind}`,
        body,
      );
      setOperation(result.operation_id);
      setRefresh((n) => n + 1);
    } catch (cause) {
      setError(message(cause));
    }
  }
  async function editConfiguration(installation: Installation) {
    try {
      const binding = installation.installed;
      if (!binding) throw new Error("Install a release before configuring it.");
      const [revision, current] = await Promise.all([
        readJson<Revision>(token, `/v1/app-revisions/${binding.revision_id}`),
        readJson<{ values: Record<string, string | number | boolean> }>(
          token,
          `/v1/app-installations/${installation.id}/configuration-revisions/${binding.configuration_revision_id}`,
        ),
      ]);
      setEditing({ installation, revision, values: current.values });
    } catch (cause) {
      setError(message(cause));
    }
  }
  async function saveConfiguration(event: React.FormEvent) {
    event.preventDefault();
    if (!editing?.installation.installed) return;
    try {
      setError("");
      const result = await mutateApp<{ operation_id: string }>(
        token,
        `/v1/app-installations/${editing.installation.id}/configure`,
        {
          expected_configuration_revision_id:
            editing.installation.installed.configuration_revision_id,
          configuration: editing.values,
        },
      );
      setOperation(result.operation_id);
      setEditing(null);
      setRefresh((n) => n + 1);
    } catch (cause) {
      setError(message(cause));
    }
  }
  const activeFor = (installation: Installation) =>
    runs.some(
      (run) =>
        run.installation_id === installation.id &&
        ["accepted", "running"].includes(run.state),
    );
  const library = [...imports]
    .sort((a, b) => a.imported_at.localeCompare(b.imported_at))
    .reduce<AppImport[]>((items, item) => {
      const existing = items.findIndex(
        (candidate) => candidate.app_id === item.app_id,
      );
      if (existing >= 0) items[existing] = item;
      else items.push(item);
      return items;
    }, [])
    .sort(
      (a, b) =>
        Number(b.app_id === focusAppId) - Number(a.app_id === focusAppId),
    );
  return (
    <main className="app-workflow-page">
      <header className="app-workflow-heading">
        <div>
          <p className="app-eyebrow">Workspace inventory</p>
          <h1>Apps</h1>
          <p>
            Imported releases, robot installations, configuration and run state.
          </p>
        </div>
        <button
          className="button-secondary"
          type="button"
          onClick={() => setRefresh((n) => n + 1)}
        >
          Refresh
        </button>
      </header>
      {error && (
        <p role="alert" className="app-feedback app-feedback-error">
          {error}
        </p>
      )}
      {operation && (
        <AppOperationStatus operationId={operation} token={token} />
      )}
      <div className="app-install-grid">
        {library.map((item) => {
          const app = apps[item.app_id];
          const installRows = installations.filter(
            (x) => x.app_id === item.app_id,
          );
          return (
            <section className="app-panel" key={item.id}>
              <p className="app-eyebrow">
                {item.source.kind === "direct_share"
                  ? "Direct share"
                  : "Store import"}
              </p>
              <h2>{app?.name ?? item.app_id}</h2>
              <p>{app?.description}</p>
              <small>
                Imported {new Date(item.imported_at).toLocaleString()}
              </small>
              <p>
                <a
                  href={`/runs?workspace=${encodeURIComponent(organizationId)}&app_id=${encodeURIComponent(item.app_id)}`}
                >
                  Open this App’s Logbook ↗
                </a>
              </p>
              {installRows.length ? (
                installRows.map((installation) => {
                  const robot = robots.find(
                    (x) => x.id === installation.robot_id,
                  );
                  return (
                    <div className="app-install-row" key={installation.id}>
                      <strong>{robot?.name ?? installation.robot_id}</strong>
                      <span>
                        {installation.installed
                          ? `Installed · ${revisionChoices[item.app_id]?.find((revision) => revision.id === installation.installed?.revision_id)?.manifest.release ?? installation.installed.revision_id}`
                          : "Not installed"}
                      </span>
                      {installation.installed && (
                        <details>
                          <summary>Current configuration</summary>
                          <dl>
                            {Object.entries(
                              configurationValues[installation.id] ?? {},
                            ).map(([key, value]) => (
                              <div key={key}>
                                <dt>{key}</dt>
                                <dd>{String(value)}</dd>
                              </div>
                            ))}
                          </dl>
                          <small>
                            Configuration revision{" "}
                            {installation.installed.configuration_revision_id}
                          </small>
                        </details>
                      )}
                      {revisionChoices[item.app_id]?.length ? (
                        <small>
                          Available releases:{" "}
                          {revisionChoices[item.app_id]
                            .map((revision) => revision.manifest.release)
                            .join(", ")}
                        </small>
                      ) : null}
                      {installation.candidate && (
                        <span>Candidate · {installation.candidate.state}</span>
                      )}
                      {installation.installed && (
                        <div className="app-actions">
                          {canManage && (
                            <>
                              <button
                                className="button-secondary"
                                type="button"
                                onClick={() =>
                                  void update(installation, "update")
                                }
                              >
                                Update
                              </button>
                              <button
                                className="button-secondary"
                                type="button"
                                onClick={() =>
                                  void editConfiguration(installation)
                                }
                              >
                                Configure
                              </button>
                            </>
                          )}
                          {canOperate && (
                            <button
                              className="button-secondary"
                              type="button"
                              onClick={() =>
                                void update(
                                  installation,
                                  activeFor(installation) ? "stop" : "start",
                                )
                              }
                            >
                              {activeFor(installation) ? "Stop" : "Start"}
                            </button>
                          )}
                        </div>
                      )}
                      {editing?.installation.id === installation.id && (
                        <form
                          className="app-config-form"
                          onSubmit={(event) => void saveConfiguration(event)}
                        >
                          <h3>
                            Configuration · {editing.revision.manifest.release}
                          </h3>
                          {Object.entries(
                            editing.revision.manifest.configuration.schema
                              .properties,
                          ).map(([key, property]) => {
                            const inputId = `${installation.id}-${key}`;
                            const required =
                              editing.revision.manifest.configuration.schema.required?.includes(
                                key,
                              ) ?? false;
                            return (
                              <div className="app-field" key={key}>
                                <label htmlFor={inputId}>{key}</label>
                                {property.enum ? (
                                  <select
                                    id={inputId}
                                    required={required}
                                    value={String(editing.values[key] ?? "")}
                                    onChange={(event) =>
                                      setEditing((old) => {
                                        if (!old) return old;
                                        const values = { ...old.values };
                                        if (event.target.value === "")
                                          delete values[key];
                                        else
                                          values[key] =
                                            property.type === "integer"
                                              ? Number(event.target.value)
                                              : property.type === "boolean"
                                                ? event.target.value === "true"
                                                : event.target.value;
                                        return { ...old, values };
                                      })
                                    }
                                  >
                                    {!required && (
                                      <option value="">Not set</option>
                                    )}
                                    {property.enum.map((value) => (
                                      <option
                                        key={String(value)}
                                        value={String(value)}
                                      >
                                        {String(value)}
                                      </option>
                                    ))}
                                  </select>
                                ) : property.type === "boolean" ? (
                                  <input
                                    id={inputId}
                                    type="checkbox"
                                    checked={Boolean(editing.values[key])}
                                    onChange={(event) =>
                                      setEditing((old) =>
                                        old
                                          ? {
                                              ...old,
                                              values: {
                                                ...old.values,
                                                [key]: event.target.checked,
                                              },
                                            }
                                          : old,
                                      )
                                    }
                                  />
                                ) : (
                                  <input
                                    id={inputId}
                                    required={required}
                                    type={
                                      property.type === "integer"
                                        ? "number"
                                        : "text"
                                    }
                                    min={property.minimum}
                                    max={property.maximum}
                                    minLength={property.minLength}
                                    maxLength={property.maxLength}
                                    value={String(editing.values[key] ?? "")}
                                    onChange={(event) =>
                                      setEditing((old) => {
                                        if (!old) return old;
                                        const values = { ...old.values };
                                        if (
                                          property.type === "integer" &&
                                          event.target.value === ""
                                        )
                                          delete values[key];
                                        else
                                          values[key] =
                                            property.type === "integer"
                                              ? Number(event.target.value)
                                              : event.target.value;
                                        return { ...old, values };
                                      })
                                    }
                                  />
                                )}
                              </div>
                            );
                          })}
                          <div className="app-actions">
                            <button className="button-primary" type="submit">
                              Save configuration
                            </button>
                            <button
                              className="button-secondary"
                              type="button"
                              onClick={() => setEditing(null)}
                            >
                              Cancel
                            </button>
                          </div>
                        </form>
                      )}
                    </div>
                  );
                })
              ) : (
                <p>No robot installations yet.</p>
              )}
              {runs.filter((x) => x.snapshot.app_id === item.app_id).length >
                0 && (
                <p>
                  {runs.filter((x) => x.snapshot.app_id === item.app_id).length}{" "}
                  recorded runs
                </p>
              )}
            </section>
          );
        })}
        {imports.length === 0 && (
          <section className="app-panel">
            <h2>No imported Apps</h2>
            <p>
              Open the App Store to discover a release and import it into this
              workspace.
            </p>
          </section>
        )}
      </div>
    </main>
  );
}

export function StoreRoute({
  organizationId,
  workspaceRole,
}: {
  organizationId: string;
  workspaceRole?: string;
}) {
  return (
    <AppStorePage
      organizationId={organizationId}
      workspaceRole={workspaceRole}
    />
  );
}
