import type { App, AppRun, Installation, Token } from "@/lib/apps";
import { appPath } from "@/lib/location";
import { StudioSection } from "@/views/StudioPage";
import { LatestCapture } from "./views/LatestCapture";
import { isCameraSnapshotLoop } from "./views/StoreHeaderImage";

export function AppOverview({
  app,
  organizationId,
  installation,
  runs,
  token,
}: {
  app: App;
  organizationId: string;
  installation?: Installation;
  runs: AppRun[];
  token: Token;
}) {
  const installationRuns = runs.filter(
    (run) => run.installation_id === installation?.id,
  );
  const currentRun = installationRuns.find(
    (run) => run.state === "running" || run.state === "accepted",
  );
  const latest = installationRuns.sort((a, b) =>
    b.requested_at.localeCompare(a.requested_at),
  )[0];
  return (
    <div className="studio-sections">
      {installation && (
        <StudioSection
          title={
            isCameraSnapshotLoop(app) ? "Latest capture" : "Published output"
          }
          titleId="app-published-output"
          description={
            isCameraSnapshotLoop(app)
              ? undefined
              : "Files published by this installation."
          }
          actions={
            <a
              className="text-sm font-medium text-signal-text"
              href={appPath(app.id, organizationId, installation.id, "files")}
            >
              Open Files →
            </a>
          }
        >
          {isCameraSnapshotLoop(app) && (
            <LatestCapture
              key={installation.id}
              appId={app.id}
              organizationId={organizationId}
              installationId={installation.id}
              token={token}
              running={!!currentRun}
              runId={latest?.id}
            />
          )}
        </StudioSection>
      )}
      {installation?.candidate?.failure && (
        <p role="alert" className="text-sm text-destructive">
          {installation.candidate.failure.message}
        </p>
      )}
    </div>
  );
}
