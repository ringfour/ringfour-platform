import { useAuth0 } from "@auth0/auth0-react";
import { useCallback, useEffect, useState } from "react";
import { RobotDetailPage } from "@/components/RobotDetailPage";
import { RobotsPage } from "@/components/RobotsPage";
import { loadOverview, type OverviewData } from "@/components/overview-data";
import { RobotWorkRow } from "@/components/RobotWorkRow";
import { resourcePath } from "@/lib/location";
import "./app-workflows.css";

export function RobotsWorkspacePage({
  organizationId,
  robotId,
  workspaceRole,
  onSelectRobot,
}: {
  organizationId: string;
  robotId?: string;
  workspaceRole?: string;
  onSelectRobot: (id?: string, organizationId?: string) => void;
}) {
  const { getAccessTokenSilently } = useAuth0();
  const token = useCallback(async () => {
    const value = await getAccessTokenSilently({
      authorizationParams: { audience: import.meta.env.VITE_AUTH0_AUDIENCE },
    });
    if (!value) throw new Error("Sign in required");
    return value;
  }, [getAccessTokenSilently]);
  const [snapshot, setSnapshot] = useState<OverviewData | null>(null);
  const [error, setError] = useState("");
  const [refresh, setRefresh] = useState(0);
  const [manage, setManage] = useState(false);
  const [search, setSearch] = useState("");
  const [presence, setPresence] = useState("");
  // biome-ignore lint/correctness/useExhaustiveDependencies: Refresh requests a fresh workspace snapshot.
  useEffect(() => {
    let live = true;
    setSnapshot(null);
    setError("");
    if (!organizationId) return;
    loadOverview(organizationId, token)
      .then((value) => {
        if (live) {
          setSnapshot(value);
          setError("");
        }
      })
      .catch((cause) => {
        if (live)
          setError(
            cause instanceof Error
              ? cause.message
              : "Robot inventory is unavailable.",
          );
      });
    return () => {
      live = false;
    };
  }, [organizationId, token, refresh]);
  if (manage)
    return (
      <main className="app-workflow-page">
        <header className="app-workflow-heading">
          <div>
            <p className="app-eyebrow">Robot access</p>
            <h1>Manage robots</h1>
            <p>Enroll a robot or rotate and revoke its device credentials.</p>
          </div>
          <button
            className="button-secondary"
            type="button"
            onClick={() => setManage(false)}
          >
            Back to robot inventory
          </button>
        </header>
        <RobotsPage
          preferredOrganizationId={organizationId}
          preferredRobotId={robotId}
          onOpenRobot={(id) => {
            setManage(false);
            onSelectRobot(id, organizationId);
          }}
          onRobotsChanged={() => setRefresh((value) => value + 1)}
        />
      </main>
    );
  if (robotId)
    return (
      <RobotDetailPage
        robotId={robotId}
        workspaceRole={workspaceRole}
        onBack={() => onSelectRobot(undefined, organizationId)}
        onManage={() => setManage(true)}
      />
    );
  const rows = (snapshot?.machines ?? []).filter(
    (robot) =>
      robot.name.toLocaleLowerCase().includes(search.toLocaleLowerCase()) &&
      (!presence ||
        (presence === "Unknown"
          ? robot.status === undefined
          : robot.status?.online === (presence === "Online"))),
  );
  return (
    <main className="app-workflow-page">
      <header className="app-workflow-heading">
        <div>
          <p className="app-eyebrow">Workspace fleet</p>
          <h1>Robots</h1>
          <p>
            Connectivity, observed inventory, installed Apps and current runs.
          </p>
        </div>
        <div className="app-actions">
          <button
            className="button-secondary"
            type="button"
            onClick={() => setManage(true)}
          >
            Manage robots
          </button>
          <button
            className="button-secondary"
            type="button"
            onClick={() => setRefresh((value) => value + 1)}
          >
            Refresh
          </button>
        </div>
      </header>
      <section className="app-panel robot-inventory-controls">
        <label className="app-field">
          Search robots
          <input
            type="search"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
            placeholder="Robot name"
          />
        </label>
        <label className="app-field">
          Connectivity
          <select
            value={presence}
            onChange={(event) => setPresence(event.target.value)}
          >
            <option value="">All robots</option>
            <option>Online</option>
            <option>Offline</option>
            <option>Unknown</option>
          </select>
        </label>
        <p>
          {snapshot
            ? `${rows.length} of ${snapshot.machines.length} robots`
            : "Loading robot inventory…"}
        </p>
      </section>
      {error && (
        <p role="alert" className="app-feedback app-feedback-error">
          {error}
        </p>
      )}
      <section className="robot-inventory-list" aria-label="Workspace robots">
        {rows.length ? (
          rows.map((robot) => (
            <RobotWorkRow
              key={robot.id}
              machine={robot}
              workspace={organizationId}
              onOpen={() => onSelectRobot(robot.id, organizationId)}
            />
          ))
        ) : snapshot ? (
          <div className="app-panel">
            <h2>
              {snapshot.machines.length
                ? "No robots match these filters"
                : "No robots in this workspace"}
            </h2>
            <p>Enroll a robot or change the current filters.</p>
            <button
              className="button-secondary"
              type="button"
              onClick={() => setManage(true)}
            >
              Manage robots
            </button>
          </div>
        ) : (
          <div className="app-panel">
            <p>{error || "Loading robot inventory…"}</p>
          </div>
        )}
      </section>
      <p className="robot-list-backlink">
        <a href={resourcePath("overview", "", organizationId)}>← Home</a>
      </p>
    </main>
  );
}
