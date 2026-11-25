import { useAuth0 } from "@auth0/auth0-react";
import { useCallback, useEffect, useState } from "react";
import { AppOperationStatus } from "@/components/AppOperationStatus";
import {
  type App,
  type AppLogbookEvent,
  type AppRun,
  type Installation,
  mutateApp,
  type Robot,
  readJson,
  readLiveRuns,
  readPage,
  type Token,
} from "@/lib/apps";
import { resourcePath } from "@/lib/location";
import { StudioTime } from "./StudioTime";
import "./robots.css";

type Status = {
  connectivity: "online" | "offline";
  last_seen_at: string | null;
  support: string;
  received_at: string | null;
  inventory: null | {
    observed_at: string;
    source: {
      kind: string;
      profile: { name: string; model: string; version: string };
    };
    capabilities: string[];
    instances: Record<string, string[]>;
  };
};
const describe = (error: unknown) =>
  error instanceof Error ? error.message : "Request failed";
export function RobotDetailPage({
  robotId,
  onBack,
  onManage,
  workspaceRole,
}: {
  robotId: string;
  onBack: () => void;
  workspaceRole?: string;
  onManage: () => void;
}) {
  const { getAccessTokenSilently } = useAuth0();
  const token = useCallback<Token>(async () => {
    const value = await getAccessTokenSilently({
      authorizationParams: { audience: import.meta.env.VITE_AUTH0_AUDIENCE },
    });
    if (!value) throw new Error("Sign in required");
    return value;
  }, [getAccessTokenSilently]);
  const [robot, setRobot] = useState<Robot | null>(null);
  const [status, setStatus] = useState<Status | null>(null);
  const [installations, setInstallations] = useState<Installation[]>([]);
  const [runs, setRuns] = useState<AppRun[]>([]);
  const [events, setEvents] = useState<AppLogbookEvent[]>([]);
  const [apps, setApps] = useState<Record<string, App>>({});
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
        const pairs = await Promise.all(
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
        );
        if (live) {
          setRobot(current);
          setStatus(presence);
          setInstallations(installed);
          setRuns(history);
          setEvents(logbook);
          setApps(Object.fromEntries(pairs));
          setError("");
        }
      } catch (cause) {
        if (live) setError(describe(cause));
      }
    })();
    return () => {
      live = false;
    };
  }, [robotId, token, refresh]);
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
  const canRun =
    workspaceRole === "owner" ||
    workspaceRole === "developer" ||
    workspaceRole === "operator";
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
              <span
                className="robot-presence"
                data-online={status.connectivity === "online"}
              >
                {status.connectivity === "online" ? "Online" : "Offline"}
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
        <button
          className="button-secondary"
          type="button"
          onClick={() => setRefresh((x) => x + 1)}
        >
          Refresh
        </button>
      </div>
      {error && (
        <p role="alert" className="text-danger">
          {error}
        </p>
      )}
      {status && (
        <section className="robot-detail-section">
          <h2>Device inventory</h2>
          <p>App protocol: {status.support.replaceAll("_", " ")}</p>
          {status.inventory ? (
            <>
              <p>
                {status.inventory.source?.profile?.name ??
                  "Robot profile unavailable"}
                {status.inventory.source?.profile?.model
                  ? ` · ${status.inventory.source.profile.model}`
                  : ""}
                {status.inventory.source?.profile?.version
                  ? ` · profile ${status.inventory.source.profile.version}`
                  : ""}
              </p>
              <p>
                Observed{" "}
                {status.inventory.observed_at ? (
                  <StudioTime value={status.inventory.observed_at} />
                ) : (
                  "time unavailable"
                )}
                {" · received "}
                {status.received_at ? (
                  <StudioTime value={status.received_at} />
                ) : (
                  "not yet"
                )}
              </p>
              <h3>Capabilities</h3>
              {status.inventory.capabilities?.length ? (
                <ul>
                  {status.inventory.capabilities.map((capability) => (
                    <li key={capability}>{capability}</li>
                  ))}
                </ul>
              ) : (
                <p>No capabilities reported.</p>
              )}
              {Object.entries(status.inventory.instances ?? {}).map(
                ([kind, names]) => (
                  <p key={kind}>
                    {kind}: {names.length ? names.join(", ") : "none"}
                  </p>
                ),
              )}
            </>
          ) : (
            <p>No Agent inventory has been observed.</p>
          )}
        </section>
      )}
      {operationId && (
        <AppOperationStatus
          operationId={operationId}
          token={token}
          onTerminal={refreshAfterOperation}
        />
      )}
      <section className="robot-detail-section">
        <header>
          <h2>Installed Apps</h2>
          <button type="button" className="button-secondary" onClick={onManage}>
            Manage robot
          </button>
        </header>
        {installations.length ? (
          installations.map((item) => {
            const app = apps[item.app_id];
            const active = runs.find(
              (run) =>
                run.installation_id === item.id &&
                ["accepted", "running"].includes(run.state),
            );
            return (
              <article className="robot-register-row" key={item.id}>
                <div>
                  <strong>{app?.name ?? item.app_id}</strong>
                  <small>
                    {item.installed
                      ? `Revision ${item.installed.revision_id}`
                      : "Not installed"}
                    {item.installed &&
                      ` · Config ${item.installed.configuration_revision_id}`}
                  </small>
                </div>
                <div>
                  <strong>{active ? active.state : "Stopped"}</strong>
                  <small>
                    {active && (
                      <a
                        href={resourcePath(
                          "runs",
                          active.id,
                          robot?.organization_id ?? "",
                        )}
                      >
                        Open run ↗
                      </a>
                    )}
                  </small>
                </div>
                {canRun && item.installed && (
                  <button
                    type="button"
                    className="button-secondary"
                    onClick={() => void action(item, active ? "stop" : "start")}
                  >
                    {active ? "Stop" : "Start"}
                  </button>
                )}
              </article>
            );
          })
        ) : (
          <p>No App installations are associated with this robot.</p>
        )}
      </section>
      <section className="robot-detail-section">
        <h2>Recent App runs</h2>
        {runs.length ? (
          [...runs]
            .sort((a, b) => b.requested_at.localeCompare(a.requested_at))
            .slice(0, 10)
            .map((run) => (
              <article className="robot-register-row" key={run.id}>
                <div>
                  <strong>{apps[run.snapshot.app_id]?.name ?? "App"}</strong>
                  <small>
                    <StudioTime value={run.requested_at} /> · {run.id}
                  </small>
                </div>
                <span>
                  {run.state}
                  {run.failure ? ` · ${run.failure.message}` : ""}
                </span>
                <a
                  href={resourcePath(
                    "runs",
                    run.id,
                    robot?.organization_id ?? "",
                  )}
                >
                  Details ↗
                </a>
              </article>
            ))
        ) : (
          <p>No App runs yet.</p>
        )}
      </section>
      <section className="robot-detail-section">
        <h2>Activity</h2>
        {events.slice(0, 30).map((event) => (
          <article className="robot-register-row" key={event.event_id}>
            <div>
              <strong>{event.type}</strong>
              <small>
                <StudioTime value={event.occurred_at} /> ·{" "}
                {(event.payload.message as string) ?? "App event"}
              </small>
            </div>
          </article>
        ))}
      </section>
    </main>
  );
}
