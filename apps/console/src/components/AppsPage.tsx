import { useAuth0 } from "@auth0/auth0-react";
import { useCallback, useEffect, useRef, useState } from "react";
import { AppActivity } from "@/components/AppActivity";
import { AppFiles } from "@/components/AppFiles";
import { AppLiveSettings } from "@/components/AppLiveSettings";
import { AppOperationStatus } from "@/components/AppOperationStatus";
import { AppOverview } from "@/components/AppOverview";
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
  readLiveRuns,
  readPage,
  type Token,
} from "@/lib/apps";
import { resourcePath, visit } from "@/lib/location";
import "./app-workflows.css";

const message = (value: unknown) =>
  value instanceof Error ? value.message : "Request failed.";

function navigateAppTab(event: React.MouseEvent<HTMLAnchorElement>) {
  if (
    event.button !== 0 ||
    event.metaKey ||
    event.ctrlKey ||
    event.shiftKey ||
    event.altKey
  )
    return;
  event.preventDefault();
  const target = new URL(event.currentTarget.href);
  visit(target.pathname + target.search);
}

export function AppsPage({
  organizationId,
  workspaceRole,
  focusAppId,
  focusSection = "overview",
}: {
  organizationId: string;
  workspaceRole?: string;
  focusAppId?: string;
  focusSection?: string;
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
  const [startupDirty, setStartupDirty] = useState(false);
  const [liveDirty, setLiveDirty] = useState(false);
  const [operation, setOperation] = useState("");
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [query, setQuery] = useState("");
  const [refresh, setRefresh] = useState(0);
  const refreshAfterOperation = useCallback(() => {
    setRefresh((value) => value + 1);
  }, []);
  const approvedNavigation = useRef(false);
  const restoringHistory = useRef(false);
  const loadedOrganization = useRef("");
  const canManage = workspaceRole === "owner" || workspaceRole === "developer";
  const canOperate = canManage || workspaceRole === "operator";
  const hasUnsavedSettings = startupDirty || liveDirty;
  useEffect(() => {
    if (!hasUnsavedSettings) return;
    const warn = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = "";
    };
    window.addEventListener("beforeunload", warn);
    return () => window.removeEventListener("beforeunload", warn);
  }, [hasUnsavedSettings]);
  useEffect(() => {
    const discard = () => {
      setStartupDirty(false);
      setLiveDirty(false);
      setEditing(null);
    };
    const beforeVisit = (event: Event) => {
      if (!hasUnsavedSettings) return;
      if (!window.confirm("Discard your unsaved settings changes?")) {
        event.preventDefault();
        return;
      }
      approvedNavigation.current = true;
      discard();
    };
    const onPopState = () => {
      if (restoringHistory.current) {
        restoringHistory.current = false;
        return;
      }
      if (approvedNavigation.current) {
        approvedNavigation.current = false;
        return;
      }
      if (
        hasUnsavedSettings &&
        !window.confirm("Discard your unsaved settings changes?")
      ) {
        restoringHistory.current = true;
        window.history.forward();
        return;
      }
      if (hasUnsavedSettings) discard();
    };
    window.addEventListener("ringfour:navigation", beforeVisit);
    window.addEventListener("popstate", onPopState);
    return () => {
      window.removeEventListener("ringfour:navigation", beforeVisit);
      window.removeEventListener("popstate", onPopState);
    };
  }, [hasUnsavedSettings]);
  // biome-ignore lint/correctness/useExhaustiveDependencies: refresh explicitly requests a new snapshot
  useEffect(() => {
    let live = true;
    const switchingOrganization = loadedOrganization.current !== organizationId;
    if (switchingOrganization) {
      loadedOrganization.current = organizationId;
      setLoading(true);
      setRefreshing(false);
      setImports([]);
      setApps({});
      setInstallations([]);
      setRobots([]);
      setRuns([]);
      setRevisionChoices({});
      setConfigurationValues({});
      setError("");
    } else {
      setRefreshing(true);
    }
    async function load() {
      try {
        const [importRows, robotRows, runRows] = await Promise.all([
          readPage<AppImport>(
            token,
            `/v1/organizations/${organizationId}/app-imports`,
          ),
          readPage<Robot>(token, `/v1/organizations/${organizationId}/robots`),
          readLiveRuns(token, `/v1/organizations/${organizationId}/app-runs`),
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
                .map(async (item) => {
                  const binding = item.installed;
                  if (!binding) return [item.id, {}] as const;
                  return [
                    item.id,
                    await readJson<AppConfiguration>(
                      token,
                      `/v1/app-installations/${item.id}/configuration-revisions/${binding.configuration_revision_id}`,
                    )
                      .then((value) => value.values)
                      .catch(() => ({})),
                  ] as const;
                }),
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
      } finally {
        if (live) {
          setLoading(false);
          setRefreshing(false);
        }
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
              .filter(
                (x) =>
                  x.id !== candidate?.revision_id &&
                  x.published_at.localeCompare(
                    revisions.find(
                      (revision) => revision.id === candidate?.revision_id,
                    )?.published_at ?? "",
                  ) > 0,
              )
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
      setStartupDirty(false);
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
      setStartupDirty(false);
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
    .sort((a, b) =>
      (apps[a.app_id]?.name ?? a.app_id).localeCompare(
        apps[b.app_id]?.name ?? b.app_id,
      ),
    );
  const visibleLibrary = library.filter((item) => {
    const app = apps[item.app_id];
    const robotNames = installations
      .filter((installation) => installation.app_id === item.app_id)
      .map(
        (installation) =>
          robots.find((robot) => robot.id === installation.robot_id)?.name ??
          "",
      )
      .join(" ");
    return `${app?.name ?? item.app_id} ${app?.description ?? ""} ${robotNames}`
      .toLocaleLowerCase()
      .includes(query.trim().toLocaleLowerCase());
  });
  const assignmentCount = library.reduce(
    (count, item) =>
      count +
      installations.filter(
        (installation) => installation.app_id === item.app_id,
      ).length,
    0,
  );
  const focusedApp = focusAppId ? apps[focusAppId] : undefined;
  const focusedImport = imports.find((item) => item.app_id === focusAppId);
  const focusedInstallations = installations.filter(
    (item) => item.app_id === focusAppId,
  );
  const selectedInstallationId = new URLSearchParams(
    window.location.search,
  ).get("installation");
  const selectedInstallation =
    focusedInstallations.find((item) => item.id === selectedInstallationId) ??
    (focusedInstallations.length === 1 ? focusedInstallations[0] : undefined);
  const focusedRobot = robots.find(
    (robot) => robot.id === selectedInstallation?.robot_id,
  );
  const detail = !!focusAppId && !!focusedImport;
  const focusedSection = ["overview", "settings", "files", "activity"].includes(
    focusSection,
  )
    ? focusSection
    : "overview";
  if (focusAppId && loading) {
    return (
      <main className="app-workflow-page app-detail-loading">
        <p role="status">Opening App…</p>
      </main>
    );
  }
  if (focusAppId && (!focusedImport || !focusedApp)) {
    return (
      <main className="app-workflow-page">
        <a
          className="app-breadcrumb"
          href={resourcePath("apps", "", organizationId)}
        >
          Apps <span aria-hidden="true">/</span>
        </a>
        <section className="app-library-empty" role="status">
          <p className="app-eyebrow">App unavailable</p>
          <h1>App details could not be opened</h1>
          <p>
            {error ||
              "This App may not be imported into the selected workspace."}
          </p>
          <div className="app-actions">
            <a
              className="button-secondary"
              href={resourcePath("apps", "", organizationId)}
            >
              Back to Apps
            </a>
            <button
              className="button-secondary"
              type="button"
              onClick={() => setRefresh((value) => value + 1)}
            >
              Try again
            </button>
          </div>
        </section>
      </main>
    );
  }
  if (!detail) {
    return (
      <main className="app-workflow-page apps-library-page">
        <header className="app-workflow-heading">
          <div>
            <p className="app-eyebrow">Workspace</p>
            <h1>Apps</h1>
            <p>
              Apps imported here, with their robot assignments and recent
              activity.
            </p>
          </div>
          <a
            className="button-primary"
            href={resourcePath("app-store", "", organizationId)}
          >
            Browse App Store
          </a>
        </header>
        {error && (
          <p role="alert" className="app-feedback app-feedback-error">
            {error}
          </p>
        )}
        {operation && (
          <AppOperationStatus
            operationId={operation}
            token={token}
            onTerminal={refreshAfterOperation}
          />
        )}
        <div className="app-library-toolbar">
          <label className="app-field" htmlFor="app-library-search">
            Find an App
            <input
              id="app-library-search"
              type="search"
              placeholder="Search Apps or robot names"
              value={query}
              onChange={(event) => setQuery(event.target.value)}
            />
          </label>
          <p>
            {library.length} App{library.length === 1 ? "" : "s"} ·{" "}
            {assignmentCount} robot assignment{assignmentCount === 1 ? "" : "s"}
          </p>
        </div>
        {loading ? (
          <p role="status" className="app-library-empty">
            Loading Apps…
          </p>
        ) : error && library.length === 0 ? (
          <div className="app-library-empty">
            <p className="app-eyebrow">Workspace Apps</p>
            <h2>Apps could not be loaded</h2>
            <p>{error}</p>
            <button
              className="button-secondary"
              type="button"
              onClick={() => setRefresh((value) => value + 1)}
            >
              Try again
            </button>
          </div>
        ) : visibleLibrary.length ? (
          <div className="app-library-list">
            {visibleLibrary.map((item) => {
              const app = apps[item.app_id];
              const appInstallations = installations.filter(
                (installation) => installation.app_id === item.app_id,
              );
              const appRuns = runs.filter(
                (run) => run.snapshot.app_id === item.app_id,
              );
              const targets = appInstallations.map((installation) => ({
                installation,
                robot: robots.find(
                  (robot) => robot.id === installation.robot_id,
                ),
              }));
              const targetPreview = targets.slice(0, 3);
              return (
                <article className="app-library-row" key={item.id}>
                  <div className="app-library-mark" aria-hidden="true">
                    {(app?.name ?? "A").slice(0, 1).toUpperCase()}
                  </div>
                  <div className="app-library-main">
                    <div className="app-library-title-row">
                      <h2>
                        <a
                          href={resourcePath(
                            "apps",
                            item.app_id,
                            organizationId,
                          )}
                        >
                          {app?.name ?? "App details unavailable"}
                        </a>
                      </h2>
                      <span className="app-library-source">
                        {item.source.kind === "direct_share"
                          ? "Shared with workspace"
                          : "Imported"}
                      </span>
                    </div>
                    <p>
                      {app?.description ??
                        "App information could not be loaded."}
                    </p>
                    <div className="app-library-meta">
                      {targets.length ? (
                        <span>
                          {targetPreview.map(({ installation, robot }) => (
                            <a
                              className="app-target-chip"
                              key={installation.id}
                              href={`${resourcePath("apps", item.app_id, organizationId)}&installation=${encodeURIComponent(installation.id)}`}
                            >
                              <strong>{robot?.name ?? "Robot"}</strong>
                              <span>
                                {installation.installed
                                  ? "Installed"
                                  : installation.candidate
                                    ? `Installing · ${installation.candidate.state}`
                                    : "Not installed"}
                              </span>
                            </a>
                          ))}
                          {targets.length > targetPreview.length && (
                            <span className="app-target-overflow">
                              +{targets.length - targetPreview.length} more
                            </span>
                          )}
                        </span>
                      ) : (
                        <span>No robot installations</span>
                      )}
                      <span>
                        {appRuns.length} recorded run
                        {appRuns.length === 1 ? "" : "s"}
                      </span>
                    </div>
                  </div>
                  <a
                    className="button-secondary app-library-open"
                    href={resourcePath("apps", item.app_id, organizationId)}
                  >
                    Open App <span aria-hidden="true">→</span>
                  </a>
                </article>
              );
            })}
          </div>
        ) : library.length ? (
          <div className="app-library-empty">
            <h2>No Apps match that search</h2>
            <p>Try an App name, description or robot name.</p>
            <button
              className="button-secondary"
              type="button"
              onClick={() => setQuery("")}
            >
              Clear search
            </button>
          </div>
        ) : (
          <div className="app-library-empty">
            <p className="app-eyebrow">Nothing imported yet</p>
            <h2>Choose an App for this workspace</h2>
            <p>
              Importing adds an App to this workspace. You can then install it
              on a robot and start it when you’re ready.
            </p>
            <a
              className="button-primary"
              href={resourcePath("app-store", "", organizationId)}
            >
              Browse App Store
            </a>
          </div>
        )}
      </main>
    );
  }
  return (
    <main className="app-workflow-page">
      <header className="app-workflow-heading">
        <div>
          {detail ? (
            <>
              <a
                className="app-breadcrumb"
                href={resourcePath("apps", "", organizationId)}
              >
                Apps <span aria-hidden="true">/</span>
              </a>
              {focusedSection === "overview" ? (
                <p className="app-eyebrow">Overview</p>
              ) : (
                <>
                  <p className="app-eyebrow">App · {focusedSection}</p>
                  <h1>{focusedApp?.name ?? focusAppId}</h1>
                  <p>{focusedApp?.description}</p>
                </>
              )}
            </>
          ) : (
            <>
              <p className="app-eyebrow">Workspace inventory</p>
              <h1>Apps</h1>
              <p>
                Imported releases, robot installations, configuration and run
                state.
              </p>
            </>
          )}
        </div>
        <button
          className="button-secondary"
          type="button"
          onClick={() => setRefresh((n) => n + 1)}
        >
          {refreshing ? "Refreshing…" : "Refresh"}
        </button>
      </header>
      {error && (
        <p role="alert" className="app-feedback app-feedback-error">
          {error}
        </p>
      )}
      {operation && (
        <AppOperationStatus
          operationId={operation}
          token={token}
          onTerminal={refreshAfterOperation}
        />
      )}
      {detail && (
        <section className="app-detail-context" aria-label="App context">
          <div className="app-detail-target">
            <label className="app-field" htmlFor="app-installation-target">
              Robot installation
              <select
                id="app-installation-target"
                value={selectedInstallation?.id ?? ""}
                onChange={(event) => {
                  const url = new URL(window.location.href);
                  if (event.target.value)
                    url.searchParams.set("installation", event.target.value);
                  else url.searchParams.delete("installation");
                  if (!visit(url.pathname + url.search))
                    event.currentTarget.value = selectedInstallation?.id ?? "";
                }}
              >
                <option value="">Select a robot</option>
                {focusedInstallations.map((installation) => {
                  const robot = robots.find(
                    (item) => item.id === installation.robot_id,
                  );
                  return (
                    <option key={installation.id} value={installation.id}>
                      {robot?.name ?? "Robot"}
                      {installation.installed
                        ? " · Installed"
                        : " · Not installed"}
                    </option>
                  );
                })}
              </select>
            </label>
            {selectedInstallation && (
              <p className="app-detail-selected">
                Selected robot: <strong>{focusedRobot?.name ?? "Robot"}</strong>
              </p>
            )}
          </div>
          <nav className="app-detail-tabs" aria-label="App sections">
            {(["overview", "settings", "files", "activity"] as const).map(
              (tab) => (
                <a
                  key={tab}
                  href={`/apps/${encodeURIComponent(focusAppId ?? "")}/${tab}?${new URLSearchParams({ workspace: organizationId, ...(selectedInstallation ? { installation: selectedInstallation.id } : {}) })}`}
                  aria-current={focusedSection === tab ? "page" : undefined}
                  onClick={navigateAppTab}
                >
                  {tab[0].toUpperCase() + tab.slice(1)}
                </a>
              ),
            )}
          </nav>
        </section>
      )}
      {detail && focusedSection === "overview" && focusedApp && (
        <div className="app-detail-content">
          <AppOverview
            app={focusedApp}
            organizationId={organizationId}
            installation={selectedInstallation}
            installationCount={focusedInstallations.length}
            robot={focusedRobot}
            installedRevision={revisionChoices[focusAppId ?? ""]?.find(
              (revision) =>
                revision.id === selectedInstallation?.installed?.revision_id,
            )}
            updateRevision={
              selectedInstallation?.installed
                ? revisionChoices[focusAppId ?? ""]
                    ?.filter((revision) => {
                      const installedRevision = revisionChoices[
                        focusAppId ?? ""
                      ]?.find(
                        (item) =>
                          item.id ===
                          selectedInstallation.installed?.revision_id,
                      );
                      return (
                        revision.published_at.localeCompare(
                          installedRevision?.published_at ?? "",
                        ) > 0
                      );
                    })
                    .sort((left, right) =>
                      right.published_at.localeCompare(left.published_at),
                    )[0]
                : undefined
            }
            runs={runs}
            apps={apps}
            token={token}
            canManage={canManage}
            canOperate={canOperate}
            onAction={(kind) => {
              if (selectedInstallation) void update(selectedInstallation, kind);
            }}
          />
        </div>
      )}
      <div className="app-install-grid">
        {library
          .filter(
            (item) =>
              (!detail || item.app_id === focusAppId) &&
              (!detail || focusedSection === "settings"),
          )
          .map((item) => {
            const app = apps[item.app_id];
            const installRows = installations.filter(
              (x) => x.app_id === item.app_id,
            );
            return (
              <section
                className={`app-panel${detail ? " app-startup-card" : ""}`}
                key={item.id}
              >
                {detail ? (
                  <>
                    <p className="app-eyebrow">Startup configuration</p>
                    <h2>Values supplied when the App starts</h2>
                    <p>
                      Saving these values creates a new immutable startup
                      configuration. Stop the exact active run before changing
                      them; running processes are never patched or restarted
                      automatically.
                    </p>
                  </>
                ) : (
                  <>
                    <p className="app-eyebrow">
                      {item.source.kind === "direct_share"
                        ? "Direct share"
                        : "Store import"}
                    </p>
                    <h2>
                      <a
                        href={resourcePath("apps", item.app_id, organizationId)}
                      >
                        {app?.name ?? item.app_id}
                      </a>
                    </h2>
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
                  </>
                )}
                {detail &&
                focusedInstallations.length > 1 &&
                !selectedInstallation ? (
                  <p>
                    Select a robot installation above to inspect or operate this
                    App.
                  </p>
                ) : installRows.length ? (
                  installRows
                    .filter(
                      (installation) =>
                        !detail ||
                        !selectedInstallation ||
                        installation.id === selectedInstallation.id,
                    )
                    .map((installation) => {
                      const robot = robots.find(
                        (x) => x.id === installation.robot_id,
                      );
                      return (
                        <div className="app-install-row" key={installation.id}>
                          <strong>
                            {robot?.name ?? installation.robot_id}
                          </strong>
                          <span className="app-install-state">
                            {installation.installed
                              ? `Installed · ${revisionChoices[item.app_id]?.find((revision) => revision.id === installation.installed?.revision_id)?.manifest.release ?? installation.installed.revision_id}`
                              : installation.candidate
                                ? `Install ${installation.candidate.state}`
                                : "Not installed"}
                          </span>
                          {installation.installed && (
                            <section className="app-startup-current">
                              <h3>Selected for the next start</h3>
                              <dl>
                                {Object.entries(
                                  configurationValues[installation.id] ?? {},
                                ).map(([key, value]) => (
                                  <div key={key}>
                                    <dt>{key.replaceAll("_", " ")}</dt>
                                    <dd>{String(value)}</dd>
                                  </div>
                                ))}
                              </dl>
                              {!Object.keys(
                                configurationValues[installation.id] ?? {},
                              ).length && (
                                <p>Startup values are not available yet.</p>
                              )}
                              <details className="app-technical-details">
                                <summary>
                                  Configuration revision details
                                </summary>
                                <code>
                                  {
                                    installation.installed
                                      .configuration_revision_id
                                  }
                                </code>
                              </details>
                            </section>
                          )}
                          {!detail && revisionChoices[item.app_id]?.length ? (
                            <small>
                              Available releases:{" "}
                              {revisionChoices[item.app_id]
                                .map((revision) => revision.manifest.release)
                                .join(", ")}
                            </small>
                          ) : null}
                          {installation.candidate && (
                            <span>
                              Candidate · {installation.candidate.state}
                            </span>
                          )}
                          {installation.installed && (
                            <div className="app-actions">
                              {canManage &&
                                (!detail || focusedSection === "overview") && (
                                  <button
                                    className="button-secondary"
                                    type="button"
                                    onClick={() =>
                                      void update(installation, "update")
                                    }
                                  >
                                    Update
                                  </button>
                                )}
                              {canManage &&
                                (!detail || focusedSection === "settings") && (
                                  <button
                                    className="button-secondary"
                                    type="button"
                                    disabled={activeFor(installation)}
                                    aria-describedby={
                                      activeFor(installation)
                                        ? `startup-active-${installation.id}`
                                        : undefined
                                    }
                                    onClick={() =>
                                      void editConfiguration(installation)
                                    }
                                  >
                                    Edit startup configuration
                                  </button>
                                )}
                              {canOperate &&
                                (!detail || focusedSection === "overview") && (
                                  <button
                                    className="button-secondary"
                                    type="button"
                                    onClick={() =>
                                      void update(
                                        installation,
                                        activeFor(installation)
                                          ? "stop"
                                          : "start",
                                      )
                                    }
                                  >
                                    {activeFor(installation) ? "Stop" : "Start"}
                                  </button>
                                )}
                            </div>
                          )}
                          {detail && activeFor(installation) && (
                            <p
                              id={`startup-active-${installation.id}`}
                              className="app-muted"
                            >
                              Stop this installation’s active run before
                              changing startup configuration.
                            </p>
                          )}
                          {editing?.installation.id === installation.id && (
                            <form
                              className="app-config-form"
                              onSubmit={(event) =>
                                void saveConfiguration(event)
                              }
                            >
                              <h3>
                                Configuration ·{" "}
                                {editing.revision.manifest.release}
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
                                    <label htmlFor={inputId}>
                                      {key.replaceAll("_", " ")}
                                    </label>
                                    {property.enum ? (
                                      <select
                                        id={inputId}
                                        required={required}
                                        value={String(
                                          editing.values[key] ?? "",
                                        )}
                                        onChange={(event) => {
                                          setStartupDirty(true);
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
                                                    ? event.target.value ===
                                                      "true"
                                                    : event.target.value;
                                            return { ...old, values };
                                          });
                                        }}
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
                                        onChange={(event) => {
                                          setStartupDirty(true);
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
                                          );
                                        }}
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
                                        value={String(
                                          editing.values[key] ?? "",
                                        )}
                                        onChange={(event) => {
                                          setStartupDirty(true);
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
                                          });
                                        }}
                                      />
                                    )}
                                  </div>
                                );
                              })}
                              <div className="app-actions">
                                <button
                                  className="button-primary"
                                  type="submit"
                                  disabled={!startupDirty}
                                >
                                  Save startup configuration
                                </button>
                                <button
                                  className="button-secondary"
                                  type="button"
                                  onClick={() => {
                                    setEditing(null);
                                    setStartupDirty(false);
                                  }}
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
                  <p>
                    No robot installations yet.{" "}
                    <a href={resourcePath("app-store", "", organizationId)}>
                      Open App Store
                    </a>{" "}
                    to choose a release and robot.
                  </p>
                )}
                {runs.filter((x) => x.snapshot.app_id === item.app_id).length >
                  0 && (
                  <p>
                    {
                      runs.filter((x) => x.snapshot.app_id === item.app_id)
                        .length
                    }{" "}
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
      {detail && focusedSection === "settings" && selectedInstallation && (
        <div className="app-settings-layout">
          <AppLiveSettings
            key={selectedInstallation.id}
            installationId={selectedInstallation.id}
            organizationId={organizationId}
            robotName={focusedRobot?.name ?? "Robot"}
            token={token}
            canManage={canManage}
            onDirtyChange={setLiveDirty}
          />
        </div>
      )}
      {detail && focusedSection === "files" && (
        <div className="app-settings-layout">
          <AppFiles
            key={`${focusAppId ?? ""}:${selectedInstallation?.id ?? "all"}`}
            appId={focusAppId ?? ""}
            organizationId={organizationId}
            installationId={selectedInstallation?.id}
            robotName={focusedRobot?.name}
            robots={robots}
            runs={runs.filter((run) => run.snapshot.app_id === focusAppId)}
            token={token}
          />
        </div>
      )}
      {detail && focusedSection === "activity" && (
        <div className="app-settings-layout">
          <AppActivity
            key={`${focusAppId ?? ""}:${selectedInstallation?.id ?? "all"}`}
            appId={focusAppId ?? ""}
            organizationId={organizationId}
            robots={robots}
            runs={runs.filter((run) => run.snapshot.app_id === focusAppId)}
            installationId={selectedInstallation?.id}
            token={token}
          />
        </div>
      )}
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
