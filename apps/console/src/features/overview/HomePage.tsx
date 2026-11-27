import { useEffect, useRef, useState } from "react";
import { PageHeading } from "@/components/PageHeading";
import { useDecisions } from "@/features/interactions/Decisions";
import {
  loadOverview,
  type OverviewData,
} from "@/features/overview/overview-data";
import { RobotWorkRow } from "@/features/robots/RobotWorkRow";
import { useApiToken } from "@/hooks/useApiToken";
import { readLiveRuns } from "@/lib/apps";
import { studioPath } from "@/lib/location";
import { RunHistoryRow } from "@/views/RunHistoryRow";
import { StudioPage, StudioSection } from "@/views/StudioPage";
import "@/features/overview/overview.css";
import "@/styles/app-workflows.css";

export function HomePage({
  organizationId,
  workspaceRole,
}: {
  organizationId: string;
  workspaceRole?: string;
}) {
  const token = useApiToken();
  const [data, setData] = useState<OverviewData | null>(null);
  const [error, setError] = useState("");
  const [refresh, setRefresh] = useState(0);
  const loadedOrganization = useRef("");
  const decisions = useDecisions(organizationId, token, refresh);
  // biome-ignore lint/correctness/useExhaustiveDependencies: Refresh explicitly requests a new workspace snapshot.
  useEffect(() => {
    if (!organizationId) return;
    let live = true;
    let timer: number | undefined;
    if (loadedOrganization.current !== organizationId) {
      loadedOrganization.current = organizationId;
      setData(null);
      setError("");
    }
    const load = async () => {
      try {
        const snapshot = await loadOverview(organizationId, token);
        if (live) {
          setData(snapshot);
          setError("");
        }
      } catch (cause) {
        if (live)
          setError(
            cause instanceof Error
              ? cause.message
              : "Workspace activity is unavailable.",
          );
      } finally {
        if (live) timer = window.setTimeout(load, 30000);
      }
    };
    void load();
    return () => {
      live = false;
      window.clearTimeout(timer);
    };
  }, [organizationId, token, refresh]);

  useEffect(() => {
    if (!organizationId) return;
    let live = true;
    let timer: number | undefined;
    const poll = async () => {
      try {
        const incoming = await readLiveRuns(
          token,
          `/v1/organizations/${encodeURIComponent(organizationId)}/app-runs`,
        );
        if (live) {
          setData((current) => {
            if (!current) return current;
            const known = new Map(current.runs.map((run) => [run.id, run]));
            const rows = incoming
              .map((run) => {
                const prior = known.get(run.id);
                const robot = current.robots.find(
                  (item) => item.id === run.robot_id,
                );
                if (!robot) return prior;
                const app =
                  current.importedApps.find(
                    (item) => item.id === run.snapshot.app_id,
                  )?.name ??
                  current.machines
                    .find((item) => item.id === run.robot_id)
                    ?.installedApps.find(
                      (item) => item.appId === run.snapshot.app_id,
                    )?.name ??
                  prior?.app ??
                  "App";
                return {
                  ...prior,
                  ...run,
                  robot,
                  app,
                  appId: run.snapshot.app_id,
                  appKey:
                    prior?.appKey ??
                    current.machines
                      .flatMap((item) => item.installedApps)
                      .find((item) => item.appId === run.snapshot.app_id)
                      ?.appKey,
                  version: prior?.version,
                  created_at: run.requested_at,
                  updated_at:
                    run.observed_at ?? run.started_at ?? run.requested_at,
                  error: run.failure ? { message: run.failure.message } : null,
                };
              })
              .filter((run): run is NonNullable<typeof run> => Boolean(run));
            const active = rows.filter((run) =>
              ["accepted", "running"].includes(run.state),
            );
            const unchanged =
              rows.length === current.runs.length &&
              rows.every((run) => {
                const previous = known.get(run.id);
                return (
                  previous?.state === run.state &&
                  previous?.observed_at === run.observed_at
                );
              });
            if (unchanged) return current;
            const machines = current.machines.map((machine) => ({
              ...machine,
              runs: rows.filter((run) => run.robot_id === machine.id),
            }));
            const recent = rows
              .filter((run) => !["accepted", "running"].includes(run.state))
              .sort((a, b) => b.updated_at.localeCompare(a.updated_at))
              .slice(0, 5);
            return {
              ...current,
              machines,
              runs: rows,
              active,
              recent,
              updated: new Date(),
            };
          });
        }
      } catch {
        // Keep the last complete snapshot visible while the live read retries.
      } finally {
        if (live)
          timer = window.setTimeout(
            poll,
            document.visibilityState === "visible" ? 1200 : 5000,
          );
      }
    };
    timer = window.setTimeout(poll, 1200);
    return () => {
      live = false;
      window.clearTimeout(timer);
    };
  }, [organizationId, token]);
  const requests = decisions.interactions ?? [];
  const needsAnswer = requests.filter((item) => !item.response);
  const submitted = requests.filter(
    (item) => item.response?.status === "submitted",
  );
  return (
    <StudioPage
      heading={
        <PageHeading
          title="Home"
          description={
            data
              ? `${data.machines.length} ${data.machines.length === 1 ? "robot" : "robots"} · ${data.machines.filter((item) => item.status?.online).length} online`
              : error
                ? "Workspace status unavailable"
                : "Loading workspace…"
          }
          actions={
            <button
              type="button"
              className="button-secondary"
              onClick={() => setRefresh((value) => value + 1)}
            >
              Refresh
            </button>
          }
        />
      }
    >
      {error && (
        <p role="alert" className="app-feedback app-feedback-error">
          {error}
          {data && " Showing the last complete snapshot."}
        </p>
      )}
      {(needsAnswer.length > 0 || decisions.interactionError) && (
        <StudioSection title="Needs attention" titleId="attention-heading">
          <div className="studio-list-surface studio-surface divide-y divide-border">
            {decisions.interactionError && (
              <p role="alert" className="p-4">
                {decisions.interactionError}
              </p>
            )}
            {needsAnswer.map((item) => (
              <article
                key={item.request_id}
                className="flex flex-col gap-3 border-l-[3px] border-l-primary px-4 py-4 sm:flex-row sm:items-center sm:justify-between"
              >
                <div>
                  <h3 className="text-sm font-medium text-foreground">
                    {item.prompt}
                  </h3>
                  <p className="text-xs text-muted-foreground">
                    {data?.robots.find((robot) => robot.id === item.robot_id)
                      ?.name ?? "Robot"}{" "}
                    · Human decision
                  </p>
                  <a
                    className="text-xs text-signal-text"
                    href={studioPath(
                      "logbook",
                      organizationId,
                      item.execution_id,
                    )}
                  >
                    Open run →
                  </a>
                </div>
                <div className="flex flex-wrap gap-2">
                  {item.choices.map((choice) => (
                    <button
                      className="button-secondary"
                      type="button"
                      key={choice.value}
                      disabled={
                        !["owner", "developer", "operator"].includes(
                          workspaceRole ?? "",
                        ) || !!decisions.submittingInteraction
                      }
                      onClick={() => void decisions.respond(item, choice.value)}
                    >
                      {choice.label}
                    </button>
                  ))}
                </div>
              </article>
            ))}
          </div>
        </StudioSection>
      )}
      {submitted.length > 0 && (
        <StudioSection
          title="Decisions sent"
          titleId="submitted-decisions-heading"
        >
          <ul className="space-y-3 text-sm">
            {submitted.map((item) => (
              <li key={item.request_id}>
                <a
                  className="text-signal-text"
                  href={studioPath(
                    "logbook",
                    organizationId,
                    item.execution_id,
                  )}
                >
                  {item.prompt}
                </a>
                <span className="app-muted">
                  {" "}
                  · Awaiting device confirmation
                </span>
              </li>
            ))}
          </ul>
        </StudioSection>
      )}
      {data && !data.machines.length ? (
        <section className="studio-surface p-8">
          <h2 className="font-display text-xl font-bold">
            Connect a robot and choose an App
          </h2>
          <p className="mt-2 max-w-xl text-sm text-muted-foreground">
            Connect your robot, then find an App that supports it.
          </p>
          <div className="mt-6 flex flex-wrap gap-3">
            <a
              className="button-primary"
              href={studioPath("robots", organizationId, "enroll")}
            >
              Connect a robot
            </a>
            <a
              className="button-secondary"
              href={studioPath("store", organizationId)}
            >
              Browse App Store
            </a>
          </div>
          <details className="mt-6 text-sm text-muted-foreground">
            <summary>Try the local Base101 simulator</summary>
            <p>
              The simulator setup is documented in ringfour-virtual/README.md.
            </p>
          </details>
        </section>
      ) : (
        <StudioSection
          title="Robots"
          titleId="robots-heading"
          className="robot-collection-section"
          actions={
            <a
              className="text-sm text-signal-text"
              href={studioPath("robots", organizationId)}
            >
              All robots →
            </a>
          }
        >
          <div className="robot-card-list">
            {data?.machines.map((machine) => (
              <RobotWorkRow
                key={machine.id}
                machine={machine}
                workspace={organizationId}
              />
            )) ?? (
              <p role="status" className="p-4 text-sm text-muted-foreground">
                Loading robots…
              </p>
            )}
          </div>
        </StudioSection>
      )}
      <StudioSection
        title="Recent runs"
        titleId="activity-heading"
        actions={
          <a
            className="text-sm text-signal-text"
            href={studioPath("logbook", organizationId)}
          >
            Open Logbook →
          </a>
        }
      >
        {data?.runs.slice(0, 5).map((run) => (
          <RunHistoryRow
            key={run.id}
            title={run.app}
            detail={run.robot.name}
            state={run.state}
            time={run.updated_at}
            href={studioPath("logbook", organizationId, run.id)}
            openOnRow
          />
        ))}
        {data && !data.runs.length && (
          <p className="app-muted">No runs recorded yet.</p>
        )}
      </StudioSection>
    </StudioPage>
  );
}
