import { AlertTriangle, Play, Square } from "lucide-react";
import { useEffect, useState } from "react";
import { StudioTime } from "@/components/StudioTime";
import { compatibilityMessage } from "@/lib/app-compatibility";
import {
  type App,
  type AppRun,
  type Compatibility,
  type Installation,
  type Robot,
  readJson,
  type Token,
} from "@/lib/apps";
import { describeCapability } from "@/lib/capabilities";
import { appPath, studioPath } from "@/lib/location";
import { runStateLabel } from "@/views/RunStatus";

type RobotAppStatus = {
  connectivity: "online" | "offline";
  last_seen_at: string | null;
  support: string;
  supports_manifest_v2: boolean | null;
  supports_live_settings: boolean | null;
  received_at: string | null;
};

const activeStates = new Set(["accepted", "running"]);

export function AppExecution({
  app,
  organizationId,
  installation,
  installationCount,
  robot,
  runs,
  apps,
  token,
  canOperate,
  pendingOperation = false,
  onAction,
}: {
  app: App;
  organizationId: string;
  installation?: Installation;
  installationCount: number;
  robot?: Robot;
  runs: AppRun[];
  apps: Record<string, App>;
  token: Token;
  canOperate: boolean;
  pendingOperation?: boolean;
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
  const canStart = Boolean(
    installation?.installed &&
      !currentRun &&
      !otherRun &&
      !pendingOperation &&
      canOperate &&
      status?.connectivity === "online" &&
      compatibility?.status === "compatible",
  );
  const canStop = Boolean(
    currentRun &&
      canOperate &&
      !pendingOperation &&
      status?.connectivity === "online",
  );

  const latest = installation
    ? runs
        .filter((run) => run.installation_id === installation.id)
        .sort((a, b) => b.requested_at.localeCompare(a.requested_at))[0]
    : undefined;
  return (
    <section
      className="studio-app-execution"
      aria-labelledby="app-run-state-title"
    >
      <div className="flex flex-wrap items-center gap-x-6 gap-y-3">
        <div>
          <p className="font-mono text-xs text-muted-foreground">
            Current execution
          </p>
          <div className="mt-1 flex items-center gap-2.5">
            <span
              className={`h-2.5 w-2.5 shrink-0 rounded-full ${currentRun && status?.connectivity === "online" ? "bg-success" : "bg-muted-foreground"}`}
            />
            <h2
              id="app-run-state-title"
              className="font-display text-xl font-medium"
            >
              {!installation
                ? installationCount > 1
                  ? "Choose a robot installation"
                  : "No robot installation"
                : currentRun
                  ? status?.connectivity === "online"
                    ? `${currentRun.state === "running" ? "Running" : "Starting"} on ${robot?.name ?? "robot"}`
                    : status?.connectivity === "offline"
                      ? `Last reported ${currentRun.state} while robot is offline`
                      : `Last observed ${currentRun.state}`
                  : "Not running"}
            </h2>
          </div>
          <p className="mt-1.5 text-sm text-muted-foreground">
            {checking
              ? "Checking robot…"
              : status?.connectivity === "online"
                ? "Robot online"
                : status?.connectivity === "offline"
                  ? "Robot offline"
                  : "Robot status unavailable"}
            {status?.last_seen_at && (
              <>
                {" "}
                · last seen <StudioTime value={status.last_seen_at} />
              </>
            )}
          </p>
          {currentRun && (
            <p className="mt-1.5 text-xs text-muted-foreground">
              Run requested <StudioTime value={currentRun.requested_at} /> ·{" "}
              <a
                href={studioPath("logbook", organizationId, currentRun.id)}
                className="text-signal-text"
              >
                Open run →
              </a>
            </p>
          )}
          {pendingOperation && (
            <p role="status" className="mt-2 text-sm text-signal-text">
              Waiting for the robot to confirm the current operation
            </p>
          )}
          {statusError && (
            <p role="status" className="text-sm text-muted-foreground">
              {statusError}
            </p>
          )}
        </div>
        <div className="shrink-0">
          {!installation?.installed ? (
            <a
              className="button-primary"
              href={studioPath("store", organizationId, app.id, {
                robot: robot?.id,
              })}
            >
              Find a release to install
            </a>
          ) : currentRun ? (
            <button
              className="button-danger inline-flex items-center gap-2"
              type="button"
              disabled={!canStop}
              onClick={() => onAction("stop")}
            >
              <Square className="h-4 w-4" />
              Stop on {robot?.name ?? "robot"}
            </button>
          ) : (
            <button
              className="button-primary inline-flex items-center gap-2"
              type="button"
              disabled={!canStart}
              onClick={() => onAction("start")}
            >
              <Play className="h-4 w-4" />
              Start on {robot?.name ?? "robot"}
            </button>
          )}
        </div>
      </div>
      {otherRun && installation && (
        <div className="border-t border-border py-4 text-sm text-muted-foreground">
          {apps[otherRun.snapshot.app_id]?.name ?? "Another App"} is using this
          robot’s execution slot.{" "}
          <a
            className="text-signal-text"
            href={appPath(
              otherRun.snapshot.app_id,
              installation.organization_id,
              otherRun.installation_id,
            )}
          >
            Open that App
          </a>
        </div>
      )}
      {compatibility && (
        <div className="border-t border-border pt-3 text-sm text-muted-foreground">
          {compatibilityMessage(compatibility.status)}
          {compatibility.missing_required.length > 0 && (
            <>
              {" "}
              Missing:{" "}
              {compatibility.missing_required
                .map((item) => describeCapability(item.capability).name)
                .join(", ")}
              .
            </>
          )}
          {status?.connectivity === "offline" && (
            <p>
              Compatibility uses the last known robot inventory; execution
              cannot be confirmed while offline.
            </p>
          )}
          {!canOperate ? (
            <p>Operator authority is required to start or stop Apps.</p>
          ) : status?.connectivity !== "online" ? (
            <p>
              {currentRun
                ? "Stop is available when the robot reconnects."
                : "Start is available when the robot is online."}
            </p>
          ) : !currentRun && compatibility.status !== "compatible" ? (
            <p>Resolve compatibility before starting.</p>
          ) : null}
        </div>
      )}
      {!currentRun &&
        latest &&
        ["failed", "interrupted"].includes(latest.state) && (
          <div className="flex flex-col gap-3 border-t border-l-[3px] border-border border-l-destructive bg-background py-4 sm:flex-row sm:items-center sm:justify-between">
            <div className="flex gap-2.5">
              <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
              <div>
                <p className="text-sm font-medium">
                  Last run {runStateLabel(latest.state)}
                </p>
                <p className="text-xs text-muted-foreground">
                  {latest.failure?.message ?? "Inspect the run for details."}
                </p>
              </div>
            </div>
            <a
              className="text-sm font-semibold text-signal-text"
              href={studioPath("logbook", organizationId, latest.id)}
            >
              Inspect run →
            </a>
          </div>
        )}
    </section>
  );
}
