import { useEffect, useState } from "react";
import { PageHeading } from "@/components/PageHeading";
import {
  loadOverview,
  type OverviewData,
} from "@/features/overview/overview-data";
import { RobotAccessPage } from "@/features/robots/RobotAccessPage";
import { RobotDetailPage } from "@/features/robots/RobotDetailPage";
import { useApiToken } from "@/hooks/useApiToken";
import { replaceQuery, setQuery, useLocation } from "@/lib/location";
import { CollectionPage } from "@/views/CollectionPage";
import "@/styles/app-workflows.css";
import "./robots.css";
import { RobotWorkRow } from "./RobotWorkRow";

export function RobotsWorkspacePage({
  organizationId,
  robotId,
  workspaceRole,
  section,
  onSelectRobot,
}: {
  organizationId: string;
  robotId?: string;
  section?: string;
  workspaceRole?: string;
  onSelectRobot: (id?: string, organizationId?: string) => void;
}) {
  const token = useApiToken();
  const [snapshot, setSnapshot] = useState<OverviewData | null>(null);
  const [error, setError] = useState("");
  const [refresh, setRefresh] = useState(0);
  const location = useLocation();
  const search = location.searchParams.get("q") ?? "";
  const presence = location.searchParams.get("connectivity") ?? "";
  // biome-ignore lint/correctness/useExhaustiveDependencies: Refresh requests a fresh workspace snapshot.
  useEffect(() => {
    let live = true;
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
  if (robotId === "enroll" || section === "credentials")
    return (
      <RobotAccessPage
        key={organizationId + robotId}
        organizationId={organizationId}
        robotId={section === "credentials" ? robotId : undefined}
        workspaceRole={workspaceRole}
      />
    );
  if (robotId)
    return (
      <RobotDetailPage
        robotId={robotId}
        organizationId={organizationId}
        workspaceRole={workspaceRole}
        onBack={() => onSelectRobot(undefined, organizationId)}
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
    <CollectionPage
      heading={
        <PageHeading
          title="Robots"
          description="Connectivity, observed inventory, installed Apps and current runs."
          actions={
            <div className="app-actions">
              <button
                className="button-secondary"
                type="button"
                onClick={() => onSelectRobot("enroll", organizationId)}
              >
                Connect a robot
              </button>
              <button
                className="button-secondary"
                type="button"
                onClick={() => setRefresh((value) => value + 1)}
              >
                Refresh
              </button>
            </div>
          }
        />
      }
      toolbar={
        <>
          <label className="app-field">
            Search robots
            <input
              type="search"
              value={search}
              onChange={(event) => replaceQuery({ q: event.target.value })}
              placeholder="Robot name"
            />
          </label>
          <label className="app-field">
            Connectivity
            <select
              value={presence}
              onChange={(event) => setQuery("connectivity", event.target.value)}
            >
              <option value="">All robots</option>
              <option>Online</option>
              <option>Offline</option>
              <option>Unknown</option>
            </select>
          </label>
          {snapshot && (
            <p className="studio-result-count">
              {rows.length} of {snapshot.machines.length}{" "}
              {snapshot.machines.length === 1 ? "robot" : "robots"}
            </p>
          )}
        </>
      }
      filterLabel="Robot filters"
    >
      {error && (
        <p role="alert" className="app-feedback app-feedback-error">
          {error}
        </p>
      )}
      <section aria-label="Workspace robots">
        {rows.length ? (
          <div className="robot-card-list">
            {rows.map((robot) => (
              <RobotWorkRow
                key={robot.id}
                machine={robot}
                workspace={organizationId}
                onOpen={() => onSelectRobot(robot.id, organizationId)}
              />
            ))}
          </div>
        ) : snapshot ? (
          <div className="space-y-3">
            <h2>
              {snapshot.machines.length
                ? "No robots match these filters"
                : "No robots in this workspace"}
            </h2>
            <p>Enroll a robot or change the current filters.</p>
            <button
              className="button-secondary"
              type="button"
              onClick={() => onSelectRobot("enroll", organizationId)}
            >
              Connect a robot
            </button>
          </div>
        ) : (
          <div className="space-y-3">
            {!error && <p role="status">Loading robot inventory…</p>}
          </div>
        )}
      </section>
    </CollectionPage>
  );
}
