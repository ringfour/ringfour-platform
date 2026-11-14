import { useAuth0 } from "@auth0/auth0-react";
import { useCallback, useEffect, useMemo, useState } from "react";

import { StatusBadge } from "@/components/ui/status-badge";
import { apiFetch, apiFetchAll } from "@/lib/api";

type Organization = { id: string; name: string; role: string };
type Robot = {
  id: string;
  name: string;
  enrolled_at: string | null;
  created_at: string;
};
type RobotStatus = {
  online: boolean;
  last_seen_at: string | null;
  daemon_version: string | null;
};
type Mission = {
  id: string;
  name: string;
  latest_version: number;
  created_at: string;
};
type View = "overview" | "robots" | "identity" | "health";

function shortId(id: string) {
  return id.length > 12 ? `${id.slice(0, 8)}…${id.slice(-4)}` : id;
}
function timeLabel(value: string | null) {
  if (!value) return "Never";
  const time = new Date(value);
  if (Number.isNaN(time.getTime())) return value;
  const seconds = Math.max(0, Math.floor((Date.now() - time.getTime()) / 1000));
  if (seconds < 60) return "Just now";
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} hr ago`;
  return time.toLocaleDateString(undefined, {
    month: "short",
    day: "numeric",
    year: "numeric",
  });
}

export function DashboardPage({
  onNavigate,
  preferredOrganizationId = "",
  onOrganizationChange,
}: {
  onNavigate: (view: View, robotId?: string) => void;
  preferredOrganizationId?: string;
  onOrganizationChange?: (id: string) => void;
}) {
  const { getAccessTokenSilently } = useAuth0();
  const getToken = useCallback(async () => {
    const token = await getAccessTokenSilently({
      authorizationParams: { audience: import.meta.env.VITE_AUTH0_AUDIENCE },
    });
    if (!token) throw new Error("No access token available");
    return token;
  }, [getAccessTokenSilently]);
  const [organizations, setOrganizations] = useState<Organization[] | null>(
    null,
  );
  const [organizationId, setOrganizationId] = useState(preferredOrganizationId);
  const [robots, setRobots] = useState<Robot[] | null>(null);
  const [missions, setMissions] = useState<Mission[] | null>(null);
  const [statuses, setStatuses] = useState<Record<string, RobotStatus | null>>(
    {},
  );
  const [orgError, setOrgError] = useState<string | null>(null);
  const [orgAttempt, setOrgAttempt] = useState(0);
  const [fleetError, setFleetError] = useState<string | null>(null);
  const [missionError, setMissionError] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [refresh, setRefresh] = useState(0);
  const [checkedAt, setCheckedAt] = useState<Date | null>(null);

  // biome-ignore lint/correctness/useExhaustiveDependencies: retry requests a new workspace list.
  useEffect(() => {
    let cancelled = false;
    apiFetchAll<Organization>(getToken, "/v1/organizations")
      .then((items) => {
        if (!cancelled) {
          setOrganizations(items);
          setOrganizationId((current) =>
            items.some((item) => item.id === current)
              ? current
              : items[0]?.id || "",
          );
          setOrgError(null);
        }
      })
      .catch((error: unknown) => {
        if (!cancelled)
          setOrgError(
            error instanceof Error
              ? error.message
              : "Unable to load workspaces.",
          );
      });
    return () => {
      cancelled = true;
    };
  }, [getToken, orgAttempt]);

  // The refresh counter deliberately triggers a fresh snapshot on demand.
  // biome-ignore lint/correctness/useExhaustiveDependencies: refresh requests another snapshot.
  useEffect(() => {
    if (!organizationId) return;
    let cancelled = false;
    setRobots(null);
    setMissions(null);
    setStatuses({});
    setFleetError(null);
    setMissionError(null);
    setCheckedAt(null);
    const load = async () => {
      const [fleet, catalog] = await Promise.allSettled([
        apiFetchAll<Robot>(
          getToken,
          `/v1/organizations/${organizationId}/robots`,
        ),
        apiFetchAll<Mission>(
          getToken,
          `/v1/organizations/${organizationId}/missions`,
        ),
      ]);
      if (cancelled) return;
      if (catalog.status === "fulfilled") setMissions(catalog.value);
      else setMissionError("Mission catalog is unavailable.");
      if (fleet.status === "rejected") {
        setFleetError("Fleet data is unavailable.");
        return;
      }
      setRobots(fleet.value);
      const results: Record<string, RobotStatus | null> = {};
      // Bound concurrent requests so a large fleet does not overwhelm the API.
      for (let index = 0; index < fleet.value.length; index += 10) {
        const group = fleet.value.slice(index, index + 10);
        await Promise.all(
          group.map(async (robot) => {
            try {
              results[robot.id] = (await apiFetch(
                getToken,
                `/v1/robots/${robot.id}/status`,
              ).then((response) => response.json())) as RobotStatus;
            } catch {
              results[robot.id] = null;
            }
          }),
        );
        if (cancelled) return;
        setStatuses({ ...results });
      }
      if (!cancelled) setCheckedAt(new Date());
    };
    void load();
    return () => {
      cancelled = true;
    };
  }, [organizationId, getToken, refresh]);

  const organization = organizations?.find(
    (item) => item.id === organizationId,
  );
  const online =
    robots?.filter((robot) => statuses[robot.id]?.online).length ?? 0;
  const known =
    robots?.filter((robot) => Object.hasOwn(statuses, robot.id)).length ?? 0;
  const unknown =
    robots?.filter(
      (robot) =>
        Object.hasOwn(statuses, robot.id) && statuses[robot.id] === null,
    ).length ?? 0;
  const filtered = useMemo(
    () =>
      (robots ?? []).filter((robot) =>
        `${robot.name} ${robot.id}`
          .toLowerCase()
          .includes(search.toLowerCase().trim()),
      ),
    [robots, search],
  );
  const latestMissions = useMemo(
    () =>
      [...(missions ?? [])]
        .sort((a, b) => b.created_at.localeCompare(a.created_at))
        .slice(0, 5),
    [missions],
  );

  return (
    <main className="dashboard-page">
      <div className="dashboard-heading">
        <div>
          <h1>Overview</h1>
          <p>Your robots and missions.</p>
        </div>
        <button
          type="button"
          className="button-secondary refresh-button"
          onClick={() => setRefresh((value) => value + 1)}
          disabled={!organizationId}
        >
          <span aria-hidden="true">↻</span> Refresh
        </button>
      </div>
      <div className="workspace-bar">
        <div className="workspace-bar__label">
          <span className="workspace-symbol" aria-hidden="true">
            ◇
          </span>
          <span>WORKSPACE</span>
        </div>
        {organizations === null ? (
          <span className="subtle">Loading workspaces…</span>
        ) : organizations.length === 0 ? (
          <span className="subtle">No workspace yet</span>
        ) : (
          <label className="workspace-select">
            <span className="sr-only">Organization</span>
            <select
              value={organizationId}
              onChange={(event) => {
                setOrganizationId(event.target.value);
                onOrganizationChange?.(event.target.value);
              }}
            >
              {organizations.map((item) => (
                <option key={item.id} value={item.id}>
                  {item.name}
                </option>
              ))}
            </select>
            <span aria-hidden="true">⌄</span>
          </label>
        )}
        <span className="workspace-role">{organization?.role ?? ""}</span>
        <span className="workspace-updated">
          {checkedAt
            ? `Updated ${checkedAt.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" })}`
            : "Status snapshot"}
        </span>
      </div>
      {orgError && (
        <div role="alert" className="notice-error">
          {orgError}{" "}
          <button
            type="button"
            className="text-action"
            onClick={() => setOrgAttempt((value) => value + 1)}
          >
            Try again
          </button>
        </div>
      )}
      {organizations?.length === 0 && (
        <div className="empty-panel">
          <strong>Start with a workspace</strong>
          <p>Create an organization to connect your first robot.</p>
          <button
            className="button-primary"
            type="button"
            onClick={() => onNavigate("identity")}
          >
            Go to workspace →
          </button>
        </div>
      )}
      {organizationId && (
        <>
          <section className="metrics-grid" aria-label="Workspace snapshot">
            <div className="metric-card metric-card--ready">
              <div className="metric-top">
                <span>ROBOTS ONLINE</span>
                <span className="metric-icon ready-icon">↗</span>
              </div>
              <div className="metric-value">
                {robots === null ? "…" : online.toString().padStart(2, "0")}
                <span className="metric-denominator">
                  {" "}
                  /{" "}
                  {robots === null
                    ? "…"
                    : robots.length.toString().padStart(2, "0")}
                </span>
              </div>
              <p>
                {robots === null
                  ? (fleetError ?? "Loading fleet")
                  : known < robots.length
                    ? "Checking device presence"
                    : unknown
                      ? `${unknown} status unavailable`
                      : "Currently connected"}
              </p>
            </div>
            <div className="metric-card">
              <div className="metric-top">
                <span>REGISTERED ROBOTS</span>
                <span className="metric-icon">⌗</span>
              </div>
              <div className="metric-value">
                {robots === null
                  ? "…"
                  : robots.length.toString().padStart(2, "0")}
              </div>
              <p>
                {robots === null
                  ? (fleetError ?? "Loading fleet")
                  : `${robots.filter((robot) => robot.enrolled_at).length} enrolled devices`}
              </p>
            </div>
            <div className="metric-card">
              <div className="metric-top">
                <span>MISSIONS</span>
                <span className="metric-icon orange-icon">↗</span>
              </div>
              <div className="metric-value">
                {missions === null
                  ? "…"
                  : missions.length.toString().padStart(2, "0")}
              </div>
              <p>
                {missionError ??
                  (missions === null ? "Loading catalog" : "In this workspace")}
              </p>
            </div>
          </section>
          <div className="dashboard-columns">
            <section className="dashboard-panel fleet-panel">
              <div className="panel-header">
                <div>
                  <h2>
                    Machine register{" "}
                    <span className="heading-count">
                      {robots === null
                        ? "…"
                        : robots.length.toString().padStart(2, "0")}
                    </span>
                  </h2>
                  <p>Your robots and their latest connection status.</p>
                </div>
                <button
                  className="text-action"
                  type="button"
                  onClick={() => onNavigate("robots")}
                >
                  Manage fleet <span>↗</span>
                </button>
              </div>
              <div className="fleet-toolbar">
                <label className="search-field">
                  <span aria-hidden="true">⌕</span>
                  <span className="sr-only">Search robots</span>
                  <input
                    type="search"
                    placeholder="Search robots…"
                    value={search}
                    onChange={(event) => setSearch(event.target.value)}
                  />
                </label>
                <span className="table-note">
                  {checkedAt ? "PRESENCE CHECKED" : "CHECKING PRESENCE"}
                </span>
              </div>
              {fleetError ? (
                <p className="table-empty" role="alert">
                  {fleetError}
                </p>
              ) : robots === null ? (
                <p className="table-empty">Loading robots…</p>
              ) : robots.length === 0 ? (
                <div className="table-empty">
                  <strong>No robots connected yet</strong>
                  <p>Pair a robot to see it in your fleet.</p>
                  <button
                    type="button"
                    className="text-action"
                    onClick={() => onNavigate("robots")}
                  >
                    Add a robot →
                  </button>
                </div>
              ) : filtered.length === 0 ? (
                <p className="table-empty">No robots match your search.</p>
              ) : (
                <div className="table-scroll">
                  <table className="dashboard-table">
                    <thead>
                      <tr>
                        <th>ROBOT</th>
                        <th>STATUS</th>
                        <th>DAEMON</th>
                        <th>LAST SEEN</th>
                        <th>
                          <span className="sr-only">Action</span>
                        </th>
                      </tr>
                    </thead>
                    <tbody>
                      {filtered.map((robot) => {
                        const status = statuses[robot.id];
                        return (
                          <tr key={robot.id}>
                            <td>
                              <div className="robot-cell">
                                <span
                                  className="robot-avatar"
                                  aria-hidden="true"
                                >
                                  ⌁
                                </span>
                                <span>
                                  <strong>{robot.name}</strong>
                                  <small>{shortId(robot.id)}</small>
                                </span>
                              </div>
                            </td>
                            <td>
                              {status ? (
                                <StatusBadge
                                  variant={
                                    status.online ? "success" : "neutral"
                                  }
                                  label={status.online ? "online" : "offline"}
                                />
                              ) : (
                                <StatusBadge
                                  variant="neutral"
                                  label={
                                    Object.hasOwn(statuses, robot.id)
                                      ? "unknown"
                                      : "checking"
                                  }
                                />
                              )}
                            </td>
                            <td className="table-mono">
                              {status?.daemon_version ?? "Unknown"}
                            </td>
                            <td className="table-muted">
                              {status
                                ? timeLabel(status.last_seen_at)
                                : "Unknown"}
                            </td>
                            <td>
                              <button
                                type="button"
                                className="table-link"
                                onClick={() => onNavigate("robots", robot.id)}
                                aria-label={`Open fleet for ${robot.name}`}
                              >
                                ↗
                              </button>
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              )}
            </section>
            <section className="dashboard-panel missions-panel">
              <div className="panel-header">
                <div>
                  <h2>Recent missions</h2>
                  <p>Published in this workspace.</p>
                </div>
              </div>
              {missionError ? (
                <p className="table-empty">{missionError}</p>
              ) : missions === null ? (
                <p className="table-empty">Loading missions…</p>
              ) : latestMissions.length === 0 ? (
                <div className="mission-empty">
                  <span className="mission-empty-icon" aria-hidden="true">
                    ◇
                  </span>
                  <strong>No missions published</strong>
                  <p>Published missions will appear here.</p>
                </div>
              ) : (
                <ul className="mission-list">
                  {latestMissions.map((mission) => (
                    <li key={mission.id}>
                      <span className="mission-icon" aria-hidden="true">
                        ↗
                      </span>
                      <span className="mission-info">
                        <strong>{mission.name}</strong>
                        <small>{shortId(mission.id)}</small>
                      </span>
                      <span className="mission-version">
                        {mission.latest_version
                          ? `v${mission.latest_version}`
                          : "No revisions"}
                      </span>
                    </li>
                  ))}
                </ul>
              )}
            </section>
          </div>
        </>
      )}
    </main>
  );
}
