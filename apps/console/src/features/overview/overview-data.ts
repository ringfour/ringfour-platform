import { apiFetch } from "@/lib/api";
import {
  type App,
  type AppImport,
  type AppRun,
  type Installation,
  type Revision,
  type Robot,
  readLiveRuns,
  readPage,
  type Token,
} from "@/lib/apps";
import type { RobotAppStatus } from "@/lib/robot-status";

type Member = { user_id: string; name: string | null };
type Presence = RobotAppStatus;
type Row = AppRun & {
  robot: Robot;
  app: string;
  appId: string;
  appKey?: string;
  version?: string;
  person?: string;
  activity?: string;
  evidence?: number;
  created_at: string;
  updated_at: string;
  error: { message: string } | null;
};
type Machine = Robot & {
  status?: {
    online: boolean;
    last_seen_at: string | null;
    support: string;
    received_at: string | null;
    inventory?: Presence["inventory"];
  };
  runs?: Row[];
  installation?: Installation;
  installedApps: {
    appId: string;
    appKey?: string;
    installationId: string;
    name: string;
    version?: string;
    lastObservedAt: string | null;
  }[];
  configured?: { app: string; version: string };
};
type Issue = {
  id: string;
  robot_id: string;
  robot: string;
  last_execution_id: string;
  run: Row;
  app: string;
  version?: string;
  desired_state: "running";
  observed_state: string;
  updated_at: string;
  last_error: { message: string } | null;
};

export type OverviewData = {
  machines: Machine[];
  robots: Robot[];
  importedApps: { id: string; name: string }[];
  runs: Row[];
  recent: Row[];
  active: Row[];
  issues: Issue[];
  updated: Date;
};

async function limited<T, R>(items: T[], read: (item: T) => Promise<R>) {
  const values: R[] = [];
  for (let index = 0; index < items.length; index += 24)
    values.push(
      ...(await Promise.all(items.slice(index, index + 24).map(read))),
    );
  return values;
}

async function optional<T>(read: Promise<T>) {
  try {
    return await read;
  } catch {
    return undefined;
  }
}

export async function loadOverview(
  organizationId: string,
  token: Token,
  { attribution = false }: { attribution?: boolean } = {},
) {
  const organization = `/v1/organizations/${encodeURIComponent(organizationId)}`;
  const [robots, runs, imports, members, me] = await Promise.all([
    readPage<Robot>(token, `${organization}/robots`),
    readLiveRuns(token, `${organization}/app-runs`),
    readPage<AppImport>(token, `${organization}/app-imports`),
    attribution
      ? optional(readPage<Member>(token, `${organization}/members`))
      : Promise.resolve(undefined),
    attribution
      ? optional(
          apiFetch(token, "/v1/me").then(
            (response) => response.json() as Promise<{ user: { id: string } }>,
          ),
        )
      : Promise.resolve(undefined),
  ]);
  const [statuses, installations] = await Promise.all([
    limited(robots, (robot) =>
      optional(
        apiFetch(token, `/v1/robots/${robot.id}/app-status`).then(
          (response) => response.json() as Promise<Presence>,
        ),
      ),
    ),
    limited(robots, (robot) =>
      optional(
        readPage<Installation>(
          token,
          `/v1/robots/${robot.id}/app-installations`,
        ),
      ),
    ),
  ]);
  const appIds = [
    ...new Set([
      ...imports.map((item) => item.app_id),
      ...runs.map((run) => run.snapshot.app_id),
      ...installations.flatMap(
        (items) => items?.map((item) => item.app_id) ?? [],
      ),
    ]),
  ];
  const apps = new Map<string, App>();
  const revisions = new Map<string, Revision>();
  const revisionIds = [
    ...new Set([
      ...runs.map((run) => run.snapshot.revision_id),
      ...installations.flatMap(
        (items) =>
          items?.flatMap((item) =>
            item.installed ? [item.installed.revision_id] : [],
          ) ?? [],
      ),
    ]),
  ];
  await Promise.all([
    ...appIds.map(async (appId) => {
      const app = await optional(
        apiFetch(token, `/v1/apps/${appId}`).then(
          (response) => response.json() as Promise<App>,
        ),
      );
      if (app) apps.set(appId, app);
    }),
    ...revisionIds.map(async (revisionId) => {
      const revision = await optional(
        apiFetch(token, `/v1/app-revisions/${revisionId}`).then(
          (response) => response.json() as Promise<Revision>,
        ),
      );
      if (revision) revisions.set(revision.id, revision);
    }),
  ]);

  const machines: Machine[] = robots.map((robot, index) => {
    const presence = statuses[index];
    const robotInstallations = installations[index] ?? [];
    const installed =
      robotInstallations.find((item) => item.installed) ??
      robotInstallations[0];
    const configuredRevision = installed?.installed?.revision_id
      ? revisions.get(installed.installed.revision_id)
      : undefined;
    const configuredApp = installed ? apps.get(installed.app_id) : undefined;
    return {
      ...robot,
      status: presence
        ? {
            online: presence.connectivity === "online",
            last_seen_at: presence.last_seen_at,
            support: presence.support,
            received_at: presence.received_at,
            inventory: presence.inventory,
          }
        : undefined,
      runs: runs
        .filter((run) => run.robot_id === robot.id)
        .map((run) => {
          const app = apps.get(run.snapshot.app_id);
          const revision = revisions.get(run.snapshot.revision_id);
          const member = members?.find(
            (item) => item.user_id === run.requested_by,
          );
          return {
            ...run,
            robot,
            app: app?.name ?? "App unavailable",
            appId: run.snapshot.app_id,
            appKey: app?.app_key,
            version: revision?.manifest.release,
            person:
              run.requested_by === me?.user.id
                ? "You"
                : (member?.name ?? undefined),
            activity: undefined,
            created_at: run.requested_at,
            updated_at:
              run.completed_at ??
              run.observed_at ??
              run.started_at ??
              run.requested_at,
            error: run.failure ? { message: run.failure.message } : null,
          };
        }),
      installation: installed,
      installedApps: robotInstallations.flatMap((item) => {
        if (!item.installed) return [];
        const app = apps.get(item.app_id);
        const revision = revisions.get(item.installed.revision_id);
        return [
          {
            appId: item.app_id,
            appKey: app?.app_key,
            installationId: item.id,
            name: app?.name ?? "App",
            version: revision?.manifest.release,
            lastObservedAt: item.last_observed_at,
          },
        ];
      }),
      configured:
        configuredApp && configuredRevision
          ? {
              app: configuredApp.name,
              version: configuredRevision.manifest.release,
            }
          : undefined,
    };
  });
  const rows = machines.flatMap((machine) => machine.runs ?? []);
  const active = rows.filter((run) =>
    ["accepted", "running"].includes(run.state),
  );
  const recent = rows
    .filter((run) =>
      ["succeeded", "failed", "stopped", "interrupted"].includes(run.state),
    )
    .sort((a, b) => b.updated_at.localeCompare(a.updated_at))
    .slice(0, 5);
  const issues: Issue[] = rows
    .filter((run) => run.state === "failed" || run.state === "interrupted")
    .map((run) => ({
      id: run.id,
      robot_id: run.robot_id,
      robot: run.robot.name,
      last_execution_id: run.id,
      run,
      app: run.app,
      version: run.version,
      desired_state: "running",
      observed_state: run.state,
      updated_at: run.updated_at,
      last_error: run.error,
    }));
  return {
    machines,
    robots,
    importedApps: imports.map((item) => ({
      id: item.app_id,
      name: apps.get(item.app_id)?.name ?? "App",
    })),
    runs: rows,
    recent,
    active,
    issues,
    updated: new Date(),
  };
}
