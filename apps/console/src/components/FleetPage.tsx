import { useAuth0 } from "@auth0/auth0-react";
import { useCallback, useEffect, useState } from "react";
import { RobotDetailPage } from "@/components/RobotDetailPage";
import { RobotsPage } from "@/components/RobotsPage";
import { apiFetchAll } from "@/lib/api";

type Organization = { id: string; name: string };
type Robot = { id: string; name: string; enrolled_at: string | null };

export function FleetPage({
  robotId,
  onSelectRobot,
  preferredOrganizationId,
  onOrganizationChange,
}: {
  robotId?: string;
  onSelectRobot: (id: string | undefined) => void;
  preferredOrganizationId: string;
  onOrganizationChange: (id: string) => void;
}) {
  const { getAccessTokenSilently } = useAuth0();
  const getToken = useCallback(async () => {
    const token = await getAccessTokenSilently({
      authorizationParams: { audience: import.meta.env.VITE_AUTH0_AUDIENCE },
    });
    if (!token) throw new Error("No access token available");
    return token;
  }, [getAccessTokenSilently]);
  const [orgs, setOrgs] = useState<Organization[] | null>(null);
  const [orgId, setOrgId] = useState(preferredOrganizationId);
  const [robots, setRobots] = useState<Robot[] | null>(null);
  const [error, setError] = useState("");
  const [manage, setManage] = useState(false);
  const [refresh, setRefresh] = useState(0);
  useEffect(() => {
    let cancelled = false;
    apiFetchAll<Organization>(getToken, "/v1/organizations")
      .then((items) => {
        if (!cancelled) {
          setOrgs(items);
          setOrgId((current) =>
            items.some((item) => item.id === current)
              ? current
              : (items[0]?.id ?? ""),
          );
        }
      })
      .catch((e) => {
        if (!cancelled)
          setError(
            e instanceof Error ? e.message : "Could not load workspaces",
          );
      });
    return () => {
      cancelled = true;
    };
  }, [getToken]);
  // Refresh reloads the list without changing the selected robot.
  // biome-ignore lint/correctness/useExhaustiveDependencies: refresh requests a new snapshot
  useEffect(() => {
    if (!orgId) return;
    let cancelled = false;
    setRobots(null);
    setError("");
    apiFetchAll<Robot>(getToken, `/v1/organizations/${orgId}/robots`)
      .then((items) => {
        if (!cancelled) setRobots(items);
      })
      .catch((e) => {
        if (!cancelled)
          setError(e instanceof Error ? e.message : "Could not load robots");
      });
    return () => {
      cancelled = true;
    };
  }, [getToken, orgId, refresh]);
  return (
    <div className="resource-layout">
      <aside className="resource-sidebar" aria-label="Robot list">
        <div className="resource-sidebar-header">
          <h1>Fleet</h1>
          <label className="resource-workspace">
            Workspace
            <select
              value={orgId}
              onChange={(event) => {
                const id = event.target.value;
                setOrgId(id);
                onOrganizationChange(id);
                onSelectRobot(undefined);
                setManage(false);
              }}
            >
              {orgs?.map((org) => (
                <option key={org.id} value={org.id}>
                  {org.name}
                </option>
              ))}
            </select>
          </label>
          <div className="resource-toolbar">
            <button
              className="button-secondary"
              type="button"
              onClick={() => {
                onSelectRobot(undefined);
                setManage(true);
              }}
            >
              Add robot
            </button>
            <button
              className="button-secondary"
              type="button"
              onClick={() => setRefresh((value) => value + 1)}
            >
              Refresh
            </button>
          </div>
        </div>
        {error && (
          <p role="alert" className="notice-error">
            {error}
          </p>
        )}
        {orgs?.length === 0 ? (
          <p>No workspace available.</p>
        ) : robots === null ? (
          <p>Loading robots…</p>
        ) : robots.length === 0 ? (
          <p>No robots in this workspace.</p>
        ) : (
          <div className="resource-list">
            {robots.map((robot) => (
              <button
                key={robot.id}
                type="button"
                className="record-row"
                aria-pressed={!manage && robotId === robot.id}
                onClick={() => {
                  onSelectRobot(robot.id);
                  setManage(false);
                }}
              >
                <strong>{robot.name}</strong>
                <span>{robot.enrolled_at ? "Enrolled" : "Not enrolled"}</span>
              </button>
            ))}
          </div>
        )}
      </aside>
      <div className="resource-content">
        {manage ? (
          <RobotsPage
            preferredOrganizationId={orgId}
            preferredRobotId={robotId}
            onRobotsChanged={() => setRefresh((value) => value + 1)}
            onOpenRobot={(id) => {
              onSelectRobot(id);
              setManage(false);
            }}
            onOrganizationChange={(id) => {
              setOrgId(id);
              onOrganizationChange(id);
              onSelectRobot(undefined);
            }}
          />
        ) : robotId ? (
          <RobotDetailPage
            key={robotId}
            robotId={robotId}
            onBack={() => onSelectRobot(undefined)}
            onManage={() => setManage(true)}
          />
        ) : (
          <main className="resource-empty">
            <h2>Fleet</h2>
            <p>Select a robot to inspect it, or add a robot.</p>
          </main>
        )}
      </div>
    </div>
  );
}
