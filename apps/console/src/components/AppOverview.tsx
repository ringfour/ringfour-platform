import { useEffect, useState } from "react";
import {
  type App,
  type AppRun,
  type Compatibility,
  type Installation,
  type Revision,
  type Robot,
  readJson,
  type Token,
} from "@/lib/apps";
import { resourcePath } from "@/lib/location";
import { StudioTime } from "./StudioTime";

type RobotAppStatus = {
  connectivity: "online" | "offline";
  last_seen_at: string | null;
  support: string;
  supports_manifest_v2: boolean | null;
  supports_live_settings: boolean | null;
  received_at: string | null;
};

const activeStates = new Set(["accepted", "running"]);

function compatibilityMessage(status: Compatibility["status"]) {
  switch (status) {
    case "compatible":
      return "This robot meets the release requirements.";
    case "daemon_upgrade_required":
      return "The robot needs an App runtime update before it can run this release.";
    case "inventory_unavailable":
      return "Robot capability inventory is not available yet.";
    case "missing_required":
      return "This robot is missing a required capability.";
    case "no_matching_image":
      return "This release has no image for the robot platform.";
    case "native_requirement_unavailable":
      return "This robot is missing a required native component.";
  }
}

export function AppOverview({
  app,
  organizationId,
  installation,
  installationCount,
  robot,
  installedRevision,
  updateRevision,
  runs,
  apps,
  token,
  canManage,
  canOperate,
  onAction,
}: {
  app: App;
  organizationId: string;
  installation?: Installation;
  installationCount: number;
  robot?: Robot;
  installedRevision?: Revision;
  updateRevision?: Revision;
  runs: AppRun[];
  apps: Record<string, App>;
  token: Token;
  canManage: boolean;
  canOperate: boolean;
  onAction: (kind: "update" | "start" | "stop") => void;
}) {
  const [status, setStatus] = useState<RobotAppStatus | null>(null);
  const [compatibility, setCompatibility] = useState<Compatibility | null>(
    null,
  );
  const [statusError, setStatusError] = useState("");
  const [checking, setChecking] = useState(false);
  const robotId = robot?.id;
  const installationId = installation?.id;
  const installedRevisionId = installation?.installed?.revision_id;
  useEffect(() => {
    if (!installationId || !robotId) {
      setStatus(null);
      setCompatibility(null);
      setStatusError("");
      setChecking(false);
      return;
    }
    let live = true;
    setChecking(true);
    setStatus(null);
    setCompatibility(null);
    setStatusError("");
    const presence = readJson<RobotAppStatus>(
      token,
      `/v1/robots/${robotId}/app-status`,
    );
    const compatible = installedRevisionId
      ? readJson<Compatibility>(
          token,
          `/v1/robots/${robotId}/app-compatibility?revision_id=${installedRevisionId}`,
        )
      : Promise.resolve(null);
    void Promise.allSettled([presence, compatible])
      .then(([presenceResult, compatibleResult]) => {
        if (!live) return;
        if (presenceResult.status === "fulfilled")
          setStatus(presenceResult.value);
        else
          setStatusError(
            presenceResult.reason instanceof Error
              ? presenceResult.reason.message
              : "Robot status is unavailable.",
          );
        if (compatibleResult.status === "fulfilled")
          setCompatibility(compatibleResult.value);
        else
          setStatusError(
            compatibleResult.reason instanceof Error
              ? compatibleResult.reason.message
              : "Compatibility is unavailable.",
          );
      })
      .finally(() => {
        if (live) setChecking(false);
      });
    return () => {
      live = false;
    };
  }, [installationId, installedRevisionId, robotId, token]);

  const currentRun = installation
    ? runs.find(
        (run) =>
          run.installation_id === installation.id &&
          activeStates.has(run.state),
      )
    : undefined;
  const otherRun =
    installation && robot
      ? runs.find(
          (run) =>
            run.robot_id === robot.id &&
            activeStates.has(run.state) &&
            run.installation_id !== installation.id,
        )
      : undefined;
  const currentRelease = installedRevision?.manifest.release;
  const canStart = Boolean(
    installation?.installed &&
      !currentRun &&
      !otherRun &&
      canOperate &&
      status?.connectivity === "online" &&
      compatibility?.status === "compatible",
  );
  const canStop = Boolean(
    currentRun && canOperate && status?.connectivity === "online",
  );

  return (
    <div className="app-overview-layout">
      <section
        className="app-overview-primary"
        aria-labelledby="app-overview-title"
      >
        <header className="app-overview-identity">
          <div className="app-overview-artwork" aria-hidden="true">
            {app.name.slice(0, 1).toUpperCase()}
          </div>
          <div>
            <p className="app-eyebrow">
              {installedRevision
                ? `Release ${installedRevision.manifest.release}`
                : "App"}
            </p>
            <h1 id="app-overview-title">{app.name}</h1>
            <p>{app.description}</p>
            <details className="app-technical-details">
              <summary>Publisher and release details</summary>
              <dl>
                <div>
                  <dt>Publisher organization</dt>
                  <dd>
                    {installedRevision?.publisher_organization_id ??
                      app.publisher_organization_id}
                  </dd>
                </div>
                {installedRevision && (
                  <div>
                    <dt>Release digest</dt>
                    <dd>{installedRevision.manifest_digest}</dd>
                  </div>
                )}
              </dl>
            </details>
          </div>
        </header>
        {!installation ? (
          <div className="app-overview-empty">
            {installationCount > 1 ? (
              <>
                <h3>Choose a robot to view this installation.</h3>
                <p>
                  This App is installed on {installationCount} robots. Select a
                  robot above to inspect its release, settings and run state.
                </p>
              </>
            ) : (
              <>
                <h3>This App is not installed on a robot in this workspace.</h3>
                <p>
                  Importing an App makes it available to the workspace.
                  Installation targets a robot; it does not start the App.
                </p>
                <a
                  className="button-primary"
                  href={resourcePath("app-store", "", organizationId)}
                >
                  Browse App Store
                </a>
              </>
            )}
          </div>
        ) : (
          <section
            className="app-run-control"
            aria-labelledby="app-run-state-title"
          >
            <div className="app-run-context">
              <p className="app-eyebrow">Selected robot</p>
              <h3>{robot?.name ?? "Robot"}</h3>
              <p
                className="app-robot-connectivity"
                data-online={status?.connectivity === "online"}
              >
                {checking && !status
                  ? "Checking robot status…"
                  : status?.connectivity === "online"
                    ? "Robot online"
                    : status?.connectivity === "offline"
                      ? "Robot offline"
                      : "Robot status unavailable"}
                {status?.last_seen_at && (
                  <span>
                    {" "}
                    · last seen <StudioTime value={status.last_seen_at} />
                  </span>
                )}
              </p>
              <p className="app-muted">
                App runtime:{" "}
                {status?.support?.replaceAll("_", " ") ?? "Checking support"}
              </p>
            </div>
            <div className="app-run-state">
              <p className="app-eyebrow">Execution</p>
              <h3 id="app-run-state-title">
                {currentRun
                  ? status?.connectivity === "online"
                    ? currentRun.state === "running"
                      ? `Running on ${robot?.name ?? "robot"}`
                      : `Starting on ${robot?.name ?? "robot"}`
                    : status?.connectivity === "offline"
                      ? `Last reported ${currentRun.state} while robot is offline`
                      : `Last observed ${currentRun.state}`
                  : "Not running"}
              </h3>
              {currentRun && (
                <p>
                  {status?.connectivity === "online"
                    ? "Run requested"
                    : "Last observation"}{" "}
                  <StudioTime
                    value={currentRun.observed_at ?? currentRun.requested_at}
                  />
                </p>
              )}
              {otherRun && (
                <p className="app-conflict-message">
                  {apps[otherRun.snapshot.app_id]?.name ?? "Another App"} is
                  using this robot’s execution slot.
                  <a
                    href={`${resourcePath("apps", otherRun.snapshot.app_id, installation.organization_id)}&installation=${encodeURIComponent(otherRun.installation_id)}`}
                  >
                    Open that App
                  </a>
                </p>
              )}
              {!otherRun && compatibility && (
                <p
                  className="app-compatibility"
                  data-compatible={compatibility.status === "compatible"}
                >
                  {compatibilityMessage(compatibility.status)}
                  {compatibility.missing_required.length > 0 && (
                    <span>
                      {" "}
                      Missing:{" "}
                      {compatibility.missing_required
                        .map((item) => item.capability)
                        .join(", ")}
                      .
                    </span>
                  )}
                </p>
              )}
              {compatibility && status?.connectivity === "offline" && (
                <p className="app-muted">
                  Compatibility uses the last known robot inventory; execution
                  cannot be confirmed while offline.
                </p>
              )}
              {statusError && (
                <p role="status" className="app-muted">
                  {statusError}
                </p>
              )}
              <div className="app-overview-actions">
                {!installation.installed ? (
                  <a
                    className="button-primary"
                    href={resourcePath("app-store", "", organizationId)}
                  >
                    Find a release to install
                  </a>
                ) : currentRun ? (
                  <button
                    className="button-danger"
                    type="button"
                    disabled={!canStop}
                    onClick={() => onAction("stop")}
                  >
                    Stop on {robot?.name ?? "robot"}
                  </button>
                ) : (
                  <button
                    className="button-primary"
                    type="button"
                    disabled={!canStart}
                    onClick={() => onAction("start")}
                  >
                    Start on {robot?.name ?? "robot"}
                  </button>
                )}
                {canManage && installation.installed && updateRevision && (
                  <button
                    className="button-secondary"
                    type="button"
                    onClick={() => onAction("update")}
                  >
                    Update release
                  </button>
                )}
                {!canOperate && (
                  <span className="app-muted">
                    Operator authority is required to start or stop Apps.
                  </span>
                )}
                {canOperate &&
                  !currentRun &&
                  !otherRun &&
                  status?.connectivity !== "online" && (
                    <span className="app-muted">
                      Start is available when the robot is online.
                    </span>
                  )}
                {canOperate &&
                  currentRun &&
                  status?.connectivity !== "online" && (
                    <span className="app-muted">
                      Stop is available when the robot reconnects.
                    </span>
                  )}
                {canOperate &&
                  !currentRun &&
                  compatibility &&
                  compatibility.status !== "compatible" && (
                    <span className="app-muted">
                      Resolve compatibility before starting.
                    </span>
                  )}
              </div>
            </div>
          </section>
        )}
      </section>
      <aside
        className="app-overview-supporting"
        aria-label="App release and installation details"
      >
        <section className="app-overview-section">
          <h3>Installation</h3>
          {installation ? (
            <dl>
              <div>
                <dt>Robot</dt>
                <dd>{robot?.name ?? "Robot"}</dd>
              </div>
              <div>
                <dt>Installed release</dt>
                <dd>
                  {currentRelease ??
                    (installation.installed
                      ? "Loading release…"
                      : "Not installed")}
                </dd>
              </div>
              <div>
                <dt>Installation state</dt>
                <dd>
                  {installation.installed
                    ? "Installed"
                    : installation.candidate
                      ? `Install ${installation.candidate.state}`
                      : "Not installed"}
                </dd>
              </div>
              {installation.last_observed_at && (
                <div>
                  <dt>Last observed</dt>
                  <dd>
                    <StudioTime value={installation.last_observed_at} />
                  </dd>
                </div>
              )}
            </dl>
          ) : (
            <p>
              Choose a robot installation to inspect its release and execution.
            </p>
          )}
        </section>
        {installation?.candidate && (
          <section className="app-overview-section">
            <h3>Operation needs attention</h3>
            <p>
              {installation.candidate.failure?.message ??
                `Installation operation is ${installation.candidate.state}.`}
            </p>
            <p className="app-muted">
              {installation.candidate.failure?.code ??
                installation.candidate.operation_id}
            </p>
          </section>
        )}
        <section className="app-overview-section">
          <h3>Available releases</h3>
          {installedRevision ? (
            <p>Installed {installedRevision.manifest.release}</p>
          ) : (
            <p>No confirmed release is installed.</p>
          )}
          {updateRevision && (
            <p>Update available · {updateRevision.manifest.release}</p>
          )}
          <a
            href={`/runs?${new URLSearchParams({ workspace: organizationId, app_id: app.id })}`}
          >
            Open App Logbook ↗
          </a>
        </section>
      </aside>
    </div>
  );
}
