// Adapted from Lovable src/routes/apps.index.tsx, source 1b99764.
import { ArrowRight, ChevronDown } from "lucide-react";
import { PageHeading } from "@/components/PageHeading";
import { AppArtwork } from "@/features/apps/views/AppArtwork";
import { AppVariant } from "@/features/apps/views/AppVariant";
import type { App, AppRun, Installation, Revision, Robot } from "@/lib/apps";
import { appPath, replaceQuery, setQuery, studioPath } from "@/lib/location";
import { CollectionPage } from "@/views/CollectionPage";
import { RunStatus } from "@/views/RunStatus";
import { Publisher } from "./Publisher";
import { hasCameraPreview, StoreHeaderImage } from "./StoreHeaderImage";
export function InstalledAppsView({
  workspace,
  apps,
  robots,
  installations,
  revisionChoices,
  runs,
  robotFilter,
  query,
  loading,
  error,
  onRetry,
}: {
  workspace: string;
  apps: Record<string, App>;
  robots: Robot[];
  installations: Installation[];
  revisionChoices: Record<string, Revision[]>;
  runs: AppRun[];
  robotFilter: string;
  query: string;
  loading: boolean;
  error: string;
  onRetry: () => void;
}) {
  const current = installations.filter(
    (item) =>
      item.installed &&
      !item.removed_at &&
      (!robotFilter || item.robot_id === robotFilter),
  );
  const installedAppIds = new Set(
    installations
      .filter((item) => item.installed && !item.removed_at)
      .map((item) => item.app_id),
  );
  const search = query.trim().toLowerCase();
  const uninstalled = robotFilter
    ? []
    : Object.values(apps).filter(
        (app) =>
          !installedAppIds.has(app.id) &&
          `${app.name} ${app.app_key} ${app.description}`
            .toLowerCase()
            .includes(search),
      );
  const visible = current.flatMap((installation) => {
    const app = apps[installation.app_id];
    const robot = robots.find((item) => item.id === installation.robot_id);
    const revision = revisionChoices[installation.app_id]?.find(
      (item) => item.id === installation.installed?.revision_id,
    );
    const description = revision?.manifest.display.description;
    return app &&
      `${app.name} ${description ?? ""} ${robot?.name ?? ""}`
        .toLowerCase()
        .includes(search)
      ? [{ installation, app, robot, description }]
      : [];
  });
  return (
    <CollectionPage
      heading={
        <PageHeading
          title="Apps"
          description="Apps published by or added to this workspace."
          actions={
            <div className="flex flex-wrap items-center gap-3">
              <div className="relative">
                <select
                  id="apps-robot-filter"
                  aria-label="Filter installed Apps by robot"
                  value={robotFilter}
                  onChange={(event) => setQuery("robot", event.target.value)}
                  className="studio-field appearance-none py-2 pl-3 pr-9 text-sm text-foreground"
                >
                  <option value="" className="bg-card">
                    All robots
                  </option>
                  {robots.map((robot) => (
                    <option key={robot.id} value={robot.id} className="bg-card">
                      {robot.name}
                    </option>
                  ))}
                </select>
                <ChevronDown className="pointer-events-none absolute right-2.5 top-1/2 h-3.5 w-3.5 text-muted-foreground -translate-y-1/2" />
              </div>
              <a
                href={studioPath("store", workspace, "", {
                  robot: robotFilter || undefined,
                })}
                className="button-secondary"
              >
                Browse Hub
              </a>
            </div>
          }
        />
      }
      toolbar={
        <label className="block max-w-sm text-sm text-muted-foreground">
          Find an App
          <input
            type="search"
            value={query}
            onChange={(event) => replaceQuery({ q: event.target.value })}
            className="studio-field mt-1.5 w-full px-3 py-2 text-foreground"
            placeholder="Search Apps"
          />
        </label>
      }
      filterLabel="App filters"
    >
      {error && (
        <p role="alert" className="text-destructive">
          {error}
        </p>
      )}
      {loading ? (
        <p role="status">Loading Apps…</p>
      ) : !(visible.length || uninstalled.length) ? (
        <div className="studio-surface p-8 text-center">
          <h2 className="font-display text-lg font-semibold text-foreground">
            {error
              ? "Apps could not be loaded"
              : search
                ? "No Apps match that search"
                : robotFilter
                  ? `No Apps installed on ${robots.find((robot) => robot.id === robotFilter)?.name ?? "this robot"}`
                  : "No Apps in this workspace yet"}
          </h2>
          <p className="mt-1.5 text-sm text-muted-foreground">
            Browse the Hub to install an App on a connected robot.
          </p>
          {error ? (
            <button
              className="button-secondary mt-5"
              type="button"
              onClick={onRetry}
            >
              Try again
            </button>
          ) : (
            <a
              href={studioPath("store", workspace, "", {
                robot: robotFilter || undefined,
              })}
              className="mt-5 inline-block rounded-[6px] bg-primary px-4 py-2 text-sm font-semibold text-primary-foreground"
            >
              Browse Hub
            </a>
          )}
        </div>
      ) : (
        <div className="store-card-grid">
          {uninstalled.map((app) => (
            <a
              key={app.id}
              href={appPath(app.id, workspace)}
              className="store-listing-card"
            >
              <div className="store-card-media">
                <StoreHeaderImage app={app} />
              </div>
              <div className="store-card-body">
                <div className="store-card-identity">
                  <AppArtwork app={app} className="h-12 w-12" />
                  <div>
                    <h2 className="store-card-title">{app.name}</h2>
                    <Publisher app={app} workspace={workspace} />
                  </div>
                </div>
                <p className="store-card-description">{app.description}</p>
                <div className="store-card-footer">
                  <span>Not installed</span>
                  <span className="store-card-link">
                    Open App <ArrowRight size={16} aria-hidden="true" />
                  </span>
                </div>
              </div>
            </a>
          ))}
          {visible.map(({ installation, app, robot, description }) => {
            const active = runs.find(
              (run) =>
                run.installation_id === installation.id &&
                ["accepted", "running"].includes(run.state),
            );
            return (
              <a
                key={installation.id}
                href={appPath(app.id, workspace, installation.id)}
                className="store-listing-card"
              >
                <div className="store-card-media">
                  <StoreHeaderImage app={app} />
                  {hasCameraPreview(app) && (
                    <span className="store-sample-caption">
                      Sample capture · Base101 simulator
                    </span>
                  )}
                </div>
                <div className="store-card-body">
                  <div className="store-card-identity">
                    <AppArtwork app={app} className="h-12 w-12" />
                    <div>
                      <h2 className="store-card-title">{app.name}</h2>
                      <Publisher app={app} workspace={workspace} />
                    </div>
                  </div>
                  {description && (
                    <p className="store-card-description">{description}</p>
                  )}
                  <AppVariant app={app} peers={Object.values(apps)} />
                  <p className="store-card-caption">
                    Installed on {robot?.name ?? "robot"}
                  </p>
                  <div className="store-card-footer">
                    {active ? (
                      <RunStatus state={active.state} />
                    ) : (
                      <span>Idle</span>
                    )}
                    <span className="store-card-link">
                      Open App <ArrowRight size={16} aria-hidden="true" />
                    </span>
                  </div>
                </div>
              </a>
            );
          })}
        </div>
      )}
    </CollectionPage>
  );
}
