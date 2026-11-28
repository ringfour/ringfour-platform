import { useCallback, useEffect, useRef, useState } from "react";
import { useApiToken } from "@/hooks/useApiToken";
import {
  type App,
  type AppImport,
  type Compatibility,
  type Installation,
  latestRevision,
  mutateApp,
  type Revision,
  type Robot,
  readJson,
  readPage,
  withQuery,
} from "@/lib/apps";
import { appPath, replaceQuery, useLocation, visit } from "@/lib/location";
import type { RobotAppStatus } from "@/lib/robot-status";

const message = (error: unknown) =>
  error instanceof Error ? error.message : "Request failed";
/** Catalog/import/install state remains workspace and installation scoped. */
export function useAppStore({
  organizationId,
  workspaceRole,
  drawer = false,
}: {
  organizationId: string;
  workspaceRole?: string;
  drawer?: boolean;
}) {
  const token = useApiToken();
  const location = useLocation();
  const selectedId = location.pathname.split("/")[2] ?? "";
  const requestedRelease = location.searchParams.get("release");
  const requestedRobot = location.searchParams.get("robot");
  const modal = location.searchParams.get("install") === "1";
  const operationId = location.searchParams.get("operation") ?? "";
  const query = location.searchParams.get("q") ?? "";
  const [statuses, setStatuses] = useState<Record<string, RobotAppStatus>>({});
  const [apps, setApps] = useState<App[] | null>(null);
  const [selectedApp, setSelectedApp] = useState<App | null>(null);
  const [releaseRows, setRevisions] = useState<Revision[]>([]);
  const [imports, setImports] = useState<AppImport[]>([]);
  const revisions = releaseRows.filter(
    (r) =>
      r.visibility === "listed" ||
      r.publisher_organization_id === organizationId ||
      imports.some((i) => i.revision_id === r.id),
  );
  const [robots, setRobots] = useState<Robot[]>([]);
  const [installations, setInstallations] = useState<Installation[]>([]);
  const [compatibilities, setCompatibilities] = useState<
    Record<string, Compatibility | string>
  >({});
  const [shareRevision, setShareRevision] = useState("");
  const [shareId, setShareId] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [refresh, setRefresh] = useState(0);
  const refreshAfterOperation = useCallback(
    () => setRefresh((value) => value + 1),
    [],
  );
  const [retry, setRetry] = useState(0);
  const openedHere = useRef(false);
  const canInstall = workspaceRole === "owner" || workspaceRole === "developer";
  const selectedRevision = requestedRelease
    ? revisions.find((item) => item.id === requestedRelease)
    : drawer
      ? latestRevision(revisions)
      : (latestRevision(revisions.filter((r) => r.visibility === "listed")) ??
        latestRevision(revisions));
  const robotId =
    requestedRobot ?? (robots.length === 1 ? robots[0].id : undefined) ?? "";
  const robot = robots.find((item) => item.id === robotId);
  const existing = installations.find(
    (item) =>
      item.robot_id === robotId &&
      item.app_id === selectedId &&
      !item.removed_at,
  );
  const compatibility = compatibilities[robotId];
  const sameRelease =
    !!selectedRevision &&
    existing?.installed?.revision_id === selectedRevision.id;
  const pending = existing?.candidate && existing.candidate.state !== "failed";
  const change = (
    values: Record<string, string | undefined>,
    replace = false,
  ) => {
    const url = new URL(window.location.href);
    for (const [key, value] of Object.entries(values))
      value ? url.searchParams.set(key, value) : url.searchParams.delete(key);
    if (replace) replaceQuery(values);
    else visit(url.pathname + url.search);
  };
  useEffect(() => {
    void refresh;
    let live = true;
    const load = async () => {
      try {
        const [catalog, imported, robotRows] = await Promise.all([
          drawer || selectedId
            ? Promise.resolve([])
            : readPage<App>(
                token,
                withQuery("/v1/store/apps", {
                  organization_id: organizationId,
                }),
              ),
          readPage<AppImport>(
            token,
            `/v1/organizations/${organizationId}/app-imports`,
          ),
          readPage<Robot>(token, `/v1/organizations/${organizationId}/robots`),
        ]);
        const installed = await Promise.all(
          robotRows.map((item) =>
            readPage<Installation>(
              token,
              `/v1/robots/${item.id}/app-installations`,
            ),
          ),
        );
        if (live) {
          const drawerIds = new Set([
            ...imported.map((entry) => entry.app_id),
            ...installed.flat().map((entry) => entry.app_id),
          ]);
          setApps(
            catalog.filter(
              (app) =>
                app.publisher_organization_id !== organizationId &&
                !drawerIds.has(app.id),
            ),
          );
          setImports(imported);
          setRobots(robotRows);
          setInstallations(installed.flat());
        }
      } catch (cause) {
        if (live) setError(message(cause));
      }
    };
    void load();
    return () => {
      live = false;
    };
  }, [organizationId, token, refresh, drawer, selectedId]);
  useEffect(() => {
    void retry;
    let live = true;
    setSelectedApp(null);
    setRevisions([]);
    setCompatibilities({});
    setError("");
    if (!selectedId) return;
    void Promise.all([
      readJson<App>(token, `/v1/apps/${selectedId}`),
      readPage<Revision>(token, `/v1/apps/${selectedId}/revisions`),
    ])
      .then(([app, rows]) => {
        if (live) {
          setSelectedApp(app);
          setRevisions(rows);
        }
      })
      .catch((cause) => {
        if (live) setError(message(cause));
      });
    return () => {
      live = false;
    };
  }, [selectedId, token, retry]);
  useEffect(() => {
    void refresh;
    let live = true;
    setCompatibilities({});
    setStatuses({});
    if (!selectedRevision) return;
    const load = () => {
      for (const item of robots.filter((r) => modal || r.id === robotId)) {
        void readJson<RobotAppStatus>(token, `/v1/robots/${item.id}/app-status`)
          .then((value) => {
            if (live)
              setStatuses((current) => ({ ...current, [item.id]: value }));
          })
          .catch(() => {});

        void readJson<Compatibility>(
          token,
          withQuery(`/v1/robots/${item.id}/app-compatibility`, {
            revision_id: selectedRevision.id,
          }),
        )
          .then((value) => {
            if (live)
              setCompatibilities((current) => ({
                ...current,
                [item.id]: value,
              }));
          })
          .catch((cause) => {
            if (live)
              setCompatibilities((current) => ({
                ...current,
                [item.id]: message(cause),
              }));
          });
      }
    };
    load();
    const timer = window.setInterval(load, 15000);
    return () => {
      window.clearInterval(timer);
      live = false;
    };
  }, [selectedRevision, robotId, modal, robots, token, refresh]);
  async function shared(event: React.FormEvent) {
    event.preventDefault();
    setError("");
    setBusy(true);
    try {
      const revision = await readJson<Revision>(
        token,
        withQuery(`/v1/app-revisions/${shareRevision.trim()}`, {
          share_id: shareId.trim(),
        }),
      );
      await mutateApp(
        token,
        `/v1/organizations/${organizationId}/app-imports`,
        { revision_id: revision.id, share_id: shareId.trim() },
      );
      visit(appPath(revision.app_id, organizationId));
      setRefresh((value) => value + 1);
    } catch (cause) {
      setError(message(cause));
    } finally {
      setBusy(false);
    }
  }
  async function install() {
    if (
      !canInstall ||
      typeof compatibility !== "object" ||
      compatibility.status !== "compatible" ||
      !selectedRevision ||
      !robot ||
      busy ||
      existing?.installed ||
      pending
    )
      return;
    setBusy(true);
    setError("");
    try {
      const result = await mutateApp<{ operation_id: string }>(
        token,
        `/v1/robots/${robot.id}/app-installations`,
        {
          revision_id: selectedRevision.id,
          share_id: null,
          configuration: selectedRevision.manifest.configuration.defaults,
        },
      );
      change(
        {
          operation: result.operation_id,
          robot: robot.id,
          release: selectedRevision.id,
          install: "1",
        },
        true,
      );
      setRefresh((value) => value + 1);
    } catch (cause) {
      setError(message(cause));
    } finally {
      setBusy(false);
    }
  }
  const close = () => {
    if (operationId) {
      openedHere.current = false;
      change({ install: undefined }, true);
    } else if (openedHere.current) {
      openedHere.current = false;
      window.history.back();
    } else change({ install: undefined }, true);
  };
  return {
    organizationId,
    token,
    location,
    selectedId,
    requestedRobot,
    modal,
    operationId,
    query,
    apps,
    selectedApp,
    revisions,
    robots,
    installations,
    compatibilities,
    shareRevision,
    shareId,
    busy,
    error,
    canInstall,
    selectedRevision,
    robotId,
    robot,
    existing,
    compatibility,
    sameRelease,
    pending,
    change,
    shared,
    install,
    close,
    refreshAfterOperation,
    statuses,
    setShareRevision,
    setShareId,
    reload: () => {
      setError("");
      setRetry((n) => n + 1);
      refreshAfterOperation();
    },
    openInstall: () => {
      openedHere.current = true;
      change({ install: "1", operation: undefined });
    },
  };
}
export type AppStoreModel = ReturnType<typeof useAppStore>;
