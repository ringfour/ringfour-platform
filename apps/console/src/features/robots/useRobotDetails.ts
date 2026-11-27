import { useCallback, useEffect, useState } from "react";
import { useApiToken } from "@/hooks/useApiToken";
import {
  type App,
  type AppLogbookEvent,
  type AppRun,
  type Installation,
  mutateApp,
  type Revision,
  type Robot,
  readJson,
  readLiveRuns,
  readPage,
} from "@/lib/apps";
import { inventoryObservation, type RobotAppStatus } from "@/lib/robot-status";

type Status = RobotAppStatus;
const describe = (error: unknown) =>
  error instanceof Error ? error.message : "Request failed";
export type RobotDetailScope = {
  robotId: string;
  organizationId?: string;
  workspaceRole?: string;
  onBack?: () => void;
  onManage?: () => void;
};
/** Authorize the robot's workspace before reading its protected App resources. */
export function useRobotDetails({
  robotId,
  organizationId,
  workspaceRole,
}: RobotDetailScope) {
  const token = useApiToken();
  const [robot, setRobot] = useState<Robot | null>(null);
  const [status, setStatus] = useState<Status | null>(null);
  const [installations, setInstallations] = useState<Installation[]>([]);
  const [runs, setRuns] = useState<AppRun[]>([]);
  const [events, setEvents] = useState<AppLogbookEvent[]>([]);
  const [apps, setApps] = useState<Record<string, App>>({});
  const [revisions, setRevisions] = useState<Record<string, Revision>>({});
  const [operationId, setOperationId] = useState("");
  const [error, setError] = useState("");
  const [refresh, setRefresh] = useState(0);
  const refreshAfterOperation = useCallback(() => {
    setRefresh((value) => value + 1);
  }, []);
  // biome-ignore lint/correctness/useExhaustiveDependencies: refresh explicitly requests a new snapshot
  useEffect(() => {
    let live = true;
    (async () => {
      try {
        const current = await readJson<Robot>(token, `/v1/robots/${robotId}`);
        if (organizationId && current.organization_id !== organizationId)
          throw new Error("Robot is unavailable in this workspace.");
        const [presence, installed, history, logbook] = await Promise.all([
          readJson<Status>(token, `/v1/robots/${robotId}/app-status`),
          readPage<Installation>(
            token,
            `/v1/robots/${robotId}/app-installations`,
          ),
          readLiveRuns(
            token,
            `/v1/organizations/${current.organization_id}/app-runs?robot_id=${robotId}`,
          ),
          readPage<AppLogbookEvent>(
            token,
            `/v1/organizations/${current.organization_id}/app-logbook?robot_id=${robotId}`,
          ),
        ]);
        const [pairs, releasePairs] = await Promise.all([
          Promise.all(
            [
              ...new Set(
                installed
                  .map((x) => x.app_id)
                  .concat(history.map((x) => x.snapshot.app_id)),
              ),
            ].map(
              async (id) =>
                [id, await readJson<App>(token, `/v1/apps/${id}`)] as const,
            ),
          ),
          Promise.all(
            [
              ...new Set(
                installed.flatMap((item) =>
                  item.installed ? [item.installed.revision_id] : [],
                ),
              ),
            ].map(
              async (id) =>
                [
                  id,
                  await readJson<Revision>(
                    token,
                    `/v1/app-revisions/${id}`,
                  ).catch(() => null),
                ] as const,
            ),
          ),
        ]);
        if (live) {
          setRobot(current);
          setStatus(presence);
          setInstallations(installed.filter((item) => !item.removed_at));
          setRuns(history);
          setEvents(logbook);
          setApps(Object.fromEntries(pairs));
          setRevisions(
            Object.fromEntries(
              releasePairs.filter(
                (entry): entry is readonly [string, Revision] =>
                  entry[1] !== null,
              ),
            ),
          );
          setError("");
        }
      } catch (cause) {
        if (live) setError(describe(cause));
      }
    })();
    return () => {
      live = false;
    };
  }, [robotId, organizationId, token, refresh]);
  async function action(installation: Installation, kind: "start" | "stop") {
    try {
      setError("");
      const activeRun = runs.find(
        (run) =>
          run.installation_id === installation.id &&
          ["accepted", "running"].includes(run.state),
      );
      const binding = installation.installed;
      if (!binding) throw new Error("This App has no installed revision.");
      const body =
        kind === "start"
          ? {
              expected_revision_id: binding.revision_id,
              expected_configuration_revision_id:
                binding.configuration_revision_id,
            }
          : { expected_run_id: activeRun?.id };
      if (kind === "stop" && !activeRun)
        throw new Error("No active App run to stop.");
      const result = await mutateApp<{ operation_id: string }>(
        token,
        `/v1/app-installations/${installation.id}/${kind}`,
        body,
      );
      setOperationId(result.operation_id);
      setRefresh((value) => value + 1);
    } catch (cause) {
      setError(describe(cause));
    }
  }
  const inventory = inventoryObservation(status ?? undefined);
  const canRun =
    workspaceRole === "owner" ||
    workspaceRole === "developer" ||
    workspaceRole === "operator";
  const activeRun = runs.find((run) =>
    ["accepted", "running"].includes(run.state),
  );
  return {
    token,
    robot,
    status,
    installations,
    runs,
    events,
    apps,
    revisions,
    operationId,
    error,
    inventory,
    canRun,
    activeRun,
    action,
    refreshAfterOperation,
  };
}
