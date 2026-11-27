import { useCallback, useEffect, useRef, useState } from "react";
import { useApiToken } from "@/hooks/useApiToken";
import { useUnsavedChanges } from "@/hooks/useUnsavedChanges";
import {
  type App,
  type AppConfiguration,
  type AppImport,
  type AppRun,
  type AppValues,
  type Installation,
  mutateApp,
  type Revision,
  type Robot,
  readJson,
  readLiveRuns,
  readPage,
} from "@/lib/apps";
import {
  replaceQuery,
  setQuery as setRouteQuery,
  useLocation,
} from "@/lib/location";
import type { AppsScope, StartupEdit } from "./model";

const message = (value: unknown) =>
  value instanceof Error ? value.message : "Request failed.";
/** App collection and exact-installation operations, independent of tab markup. */
export function useAppsController({
  organizationId,
  workspaceRole,
  focusAppId,
  focusSection = "overview",
}: AppsScope) {
  const token = useApiToken();
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
  const [editing, setEditing] = useState<StartupEdit | null>(null);
  const [startupError, setStartupError] = useState("");
  const [startupDirty, setStartupDirty] = useState(false);
  const [liveDirty, setLiveDirty] = useState(false);
  const [changing, setChanging] = useState(false);
  const location = useLocation();
  const operation = location.searchParams.get("operation") ?? "";
  const setOperation = (id: string) => {
    setChanging(true);
    replaceQuery({ operation: id });
  };
  const robotFilter = location.searchParams.get("robot") ?? "";
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [, setRefreshing] = useState(false);
  const query = location.searchParams.get("q") ?? "";
  const [refresh, setRefresh] = useState(0);
  const refreshAfterOperation = useCallback(() => {
    setChanging(false);
    setRefresh((value) => value + 1);
  }, []);
  const loadedOrganization = useRef("");
  const canManage = workspaceRole === "owner" || workspaceRole === "developer";
  const canOperate = canManage || workspaceRole === "operator";
  const discardSettings = useCallback(() => {
    setStartupDirty(false);
    setLiveDirty(false);
    setEditing(null);
  }, []);
  useUnsavedChanges(startupDirty || liveDirty, discardSettings);
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

  async function update(
    installation: Installation,
    kind: "update" | "start" | "stop",
  ) {
    if (kind === "update") {
      setRouteQuery("manage", "update");
      return;
    }
    setError("");
    try {
      const binding = installation.installed;
      if (!binding)
        throw new Error("This installation has no installed release.");
      const run = runs.find(
        (item) =>
          item.installation_id === installation.id &&
          ["accepted", "running"].includes(item.state),
      );
      if (kind === "stop" && !run)
        throw new Error("There is no active run to stop.");
      const accepted = await mutateApp<{ operation_id: string }>(
        token,
        `/v1/app-installations/${installation.id}/${kind}`,
        kind === "stop"
          ? { expected_run_id: run?.id }
          : {
              expected_revision_id: binding.revision_id,
              expected_configuration_revision_id:
                binding.configuration_revision_id,
            },
      );
      setOperation(accepted.operation_id);
      setRefresh((value) => value + 1);
    } catch (cause) {
      setError(message(cause));
    }
  }
  async function editConfiguration(installation: Installation) {
    try {
      setStartupError("");
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
      setStartupError(message(cause));
    }
  }
  async function saveConfiguration(event: React.FormEvent) {
    event.preventDefault();
    if (!editing?.installation.installed) return;
    try {
      setStartupError("");
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
      setStartupError(message(cause));
    }
  }
  const activeFor = (installation: Installation) =>
    runs.some(
      (run) =>
        run.installation_id === installation.id &&
        ["accepted", "running"].includes(run.state),
    );
  const focusedApp = focusAppId ? apps[focusAppId] : undefined;
  const focusedImport = imports.find((item) => item.app_id === focusAppId);
  const focusedInstallations = installations.filter(
    (item) => item.app_id === focusAppId,
  );
  const selectedInstallationId = new URLSearchParams(
    window.location.search,
  ).get("installation");
  const currentInstallations = focusedInstallations.filter(
    (item) => !item.removed_at,
  );
  const selectedInstallation = selectedInstallationId
    ? focusedInstallations.find((item) => item.id === selectedInstallationId)
    : currentInstallations.length === 1
      ? currentInstallations[0]
      : undefined;
  const focusedRobot = robots.find(
    (robot) => robot.id === selectedInstallation?.robot_id,
  );
  const detail = !!focusAppId && !!focusedImport;
  const focusedSection = ["overview", "settings", "files", "activity"].includes(
    focusSection,
  )
    ? focusSection
    : "overview";

  const changeStartup = (values: AppValues) => {
    setStartupDirty(true);
    setEditing((old) => (old ? { ...old, values } : old));
  };
  const cancelStartup = () => {
    setEditing(null);
    setStartupDirty(false);
    setStartupError("");
  };
  return {
    token,
    operation,
    error,
    loading,
    changing,
    snapshot: {
      apps,
      robots,
      installations,
      runs,
      revisionChoices,
      configurationValues,
    },
    selection: {
      robotFilter,
      query,
      focusedApp,
      focusedImport,
      focusedInstallations,
      selectedInstallationId,
      selectedInstallation,
      focusedRobot,
      detail,
      focusedSection,
    },
    permissions: { canManage, canOperate },
    actions: {
      refresh: refreshAfterOperation,
      run: update,
      setChanging,
      markLiveDirty: setLiveDirty,
    },
    startup: {
      editing,
      dirty: startupDirty,
      error: startupError,
      activeFor,
      edit: editConfiguration,
      save: saveConfiguration,
      change: changeStartup,
      cancel: cancelStartup,
    },
  };
}
