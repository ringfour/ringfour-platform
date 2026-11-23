import "./robots.css";
import { useAuth0 } from "@auth0/auth0-react";
import { useCallback, useEffect, useState } from "react";
import { RobotDetailPage } from "@/components/RobotDetailPage";
import { RobotsPage } from "@/components/RobotsPage";
import { loadOverview, type OverviewData } from "./overview-data";
import { StudioTime } from "./StudioTime";
import { RobotResources } from "./RobotResources";
import { resourcePath } from "@/lib/location";
import { apiFetchAll } from "@/lib/api";
import { setQuery, useLocation } from "@/lib/location";

type Organization = { id: string; name: string; role?: string };
type Robot = OverviewData["machines"][number];

export function FleetPage({
  robotId,
  onSelectRobot,
  preferredOrganizationId,
  onOrganizationChange,
}: {
  robotId?: string;
  onSelectRobot: (id: string | undefined, workspace?: string) => void;
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
  const url = useLocation();
  const search = url.searchParams.get("q") ?? "";
  const connection = url.searchParams.get("connection") ?? "";
  const occupancy = url.searchParams.get("occupancy") ?? "";
  const robotRows = (robots ?? []).filter((machine) => {
    const presence =
      machine.status?.online === true
        ? "Online"
        : machine.status?.online === false
          ? "Offline"
          : "Unknown";
    const work = machine.runs?.some((r) =>
      ["running", "queued", "preparing"].includes(r.state),
    )
      ? "Active run"
      : machine.runs
        ? "No active run"
        : "Unknown";
    return (
      machine.name.toLowerCase().includes(search.toLowerCase()) &&
      (!connection || presence === connection) &&
      (!occupancy || occupancy === work)
    );
  });
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
    if (!orgId || robotId || manage) return;
    let cancelled = false;
    setRobots(null);
    setError("");
    loadOverview(orgId, getToken)
      .then((items) => {
        if (!cancelled) setRobots(items.machines);
      })
      .catch((e) => {
        if (!cancelled)
          setError(e instanceof Error ? e.message : "Could not load robots");
      });
    return () => {
      cancelled = true;
    };
  }, [getToken, orgId, refresh, robotId, manage]);
  return (
    <div className="robot-workspace resource-list-page">
      {!robotId && !manage && (
        <aside className="resource-sidebar" aria-label="Robot list">
          <div className="resource-sidebar-header">
            <div className="robot-list-heading">
              <h1>Robots</h1>{" "}
              <div className="resource-toolbar">
                <button
                  className="button-primary"
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
            <div className="robot-filters">
              <label className="resource-workspace">
                Search robots
                <input
                  type="search"
                  value={search}
                  onChange={(event) => setQuery("q", event.target.value)}
                />
              </label>
              <details className="robot-filter-more">
                <summary>More filters</summary>
                <div className="robot-filter-extra">
                  {" "}
                  <label className="resource-workspace">
                    Connection{" "}
                    <select
                      value={connection}
                      onChange={(e) => setQuery("connection", e.target.value)}
                    >
                      <option value="">All</option>
                      {["Online", "Offline", "Unknown"].map((value) => (
                        <option key={value}>{value}</option>
                      ))}
                    </select>
                  </label>
                  <label className="resource-workspace">
                    Occupancy{" "}
                    <select
                      value={occupancy}
                      onChange={(e) => setQuery("occupancy", e.target.value)}
                    >
                      <option value="">All</option>
                      {["Active run", "No active run", "Unknown"].map(
                        (value) => (
                          <option key={value}>{value}</option>
                        ),
                      )}
                    </select>
                  </label>
                </div>
              </details>
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
              {robotRows.length === 0 && <p>No robots match these filters.</p>}
              <div className="robot-register-head" aria-hidden="true">
                <span>Robot</span>
                <span>Current work</span>
                <span>Status</span>
                <span />
              </div>
              {robotRows.map((machine) => {
                const active = machine.runs?.find((run) =>
                  ["running", "queued", "preparing"].includes(run.state),
                );
                return (
                  <article className="robot-register-row" key={machine.id}>
                    <div>
                      <strong>{machine.name}</strong>
                      <small>
                        {machine.status?.last_seen_at ? (
                          <>
                            Last seen{" "}
                            <StudioTime value={machine.status.last_seen_at} />
                          </>
                        ) : (
                          "No observation yet"
                        )}
                      </small>
                    </div>
                    <div>
                      <strong>{active ? active.app : "No app running"}</strong>
                      <small>
                        {active ? (
                          <>
                            <a
                              href={resourcePath("runs", active.id, orgId)}
                              aria-label={`View ${active.app} run`}
                            >
                              {active.state} ↗
                            </a>
                            {active.activity && ` · ${active.activity}`}
                          </>
                        ) : machine.deployment ? (
                          `Deployment · ${machine.deployment.observed_state}`
                        ) : (
                          "No deployment reported"
                        )}
                      </small>
                    </div>
                    <div>
                      <span
                        className="robot-presence"
                        data-online={machine.status?.online === true}
                      >
                        {machine.status?.online === true
                          ? "Online"
                          : machine.status?.online === false
                            ? "Offline"
                            : "Unknown"}
                      </span>
                      <RobotResources />
                    </div>
                    <button
                      type="button"
                      className="text-action robot-register-open"
                      aria-label={`View ${machine.name}`}
                      onClick={() => onSelectRobot(machine.id, orgId)}
                    >
                      →
                    </button>
                  </article>
                );
              })}
            </div>
          )}
        </aside>
      )}
      <div className="resource-content">
        {manage ? (
          <>
            <button
              className="text-action"
              type="button"
              onClick={() => setManage(false)}
            >
              ← Back to robots
            </button>
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
          </>
        ) : robotId ? (
          <RobotDetailPage
            key={robotId}
            robotId={robotId}
            role={orgs?.find((org) => org.id === orgId)?.role}
            onBack={() => onSelectRobot(undefined)}
            onManage={() => setManage(true)}
          />
        ) : null}
      </div>
    </div>
  );
}
