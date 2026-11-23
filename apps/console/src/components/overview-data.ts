import { apiFetch, apiFetchAll } from "@/lib/api";

type Token = () => Promise<string>;
type Named = { id: string; name: string };
type Run = {
  id: string;
  state: string;
  mission_revision_id: string | null;
  actor_user_id: string | null;
  created_at: string;
  updated_at: string;
  error?: { message: string } | null;
};
type Deployment = {
  id: string;
  robot_id: string;
  updated_at?: string;
  desired_state: string;
  observed_state: string;
  selected_revision_id: string | null;
  last_execution_id: string | null;
  last_error: { message: string } | null;
};
type Revision = { id: string; version: number };
type Entry = {
  occurred_at: string;
  type: string;
  payload: { message?: unknown };
};
type Row = Run & {
  robot: Named;
  app: string;
  version?: number;
  person?: string;
  appId?: string;
  activity?: string;
  evidence?: number;
};
export type OverviewData = Awaited<ReturnType<typeof loadOverview>>;

async function optional<T>(request: Promise<T>) {
  try {
    return await request;
  } catch {
    return undefined;
  }
}
async function mapLimited<T, R>(items: T[], read: (item: T) => Promise<R>) {
  const result: R[] = [];
  for (let i = 0; i < items.length; i += 6)
    result.push(...(await Promise.all(items.slice(i, i + 6).map(read))));
  return result;
}
export async function loadOverview(org: string, token: Token) {
  const base = "/v1/organizations/" + encodeURIComponent(org);
  const json = async <T>(path: string): Promise<T> =>
    (await apiFetch(token, path)).json();
  const [robots, apps, deployments, members, me] = await Promise.all([
    apiFetchAll<Named>(token, base + "/robots"),
    optional(apiFetchAll<Named>(token, base + "/missions")),
    optional(apiFetchAll<Deployment>(token, base + "/deployments")),
    optional(
      apiFetchAll<{ user_id: string; name: string | null }>(
        token,
        base + "/members",
      ),
    ),
    optional(json<{ user: { id: string } }>("/v1/me")),
  ]);
  const versions = new Map<
    string,
    { app: string; appId: string; version: number }
  >();
  await mapLimited(apps ?? [], async (app) => {
    for (const v of (await optional(
      apiFetchAll<Revision>(token, "/v1/missions/" + app.id + "/revisions"),
    )) ?? [])
      versions.set(v.id, { app: app.name, appId: app.id, version: v.version });
  });
  const machines = await mapLimited(robots, async (robot) => {
    const [status, history] = await Promise.all([
      optional(
        json<{ online: boolean; last_seen_at: string | null }>(
          "/v1/robots/" + robot.id + "/status",
        ),
      ),
      optional(
        apiFetchAll<Run>(token, "/v1/robots/" + robot.id + "/executions"),
      ),
    ]);
    const deployment = deployments?.find((item) => item.robot_id === robot.id);
    const runs: Row[] | undefined = history?.map((run) => ({
      ...run,
      robot,
      app: run.mission_revision_id ? "Unknown app" : "Direct run",
      ...versions.get(run.mission_revision_id ?? ""),
      person:
        run.actor_user_id === me?.user?.id
          ? "You"
          : (members?.find((m) => m.user_id === run.actor_user_id)?.name ??
            undefined),
    }));
    return {
      ...robot,
      status,
      runs,
      deployment,
      configured: versions.get(deployment?.selected_revision_id ?? ""),
    };
  });
  const runs = machines.flatMap((machine) => machine.runs ?? []);
  const active = runs.filter((row) =>
    ["running", "preparing", "queued"].includes(row.state),
  );
  const recent = runs
    .filter((row) => ["succeeded", "failed", "cancelled"].includes(row.state))
    .sort((a, b) => b.updated_at.localeCompare(a.updated_at))
    .slice(0, 3);
  await mapLimited([...active, ...recent], async (row) => {
    const ended = recent.includes(row);
    const [events, files] = await Promise.all([
      optional(
        apiFetchAll<Entry>(
          token,
          base + "/events?execution_id=" + encodeURIComponent(row.id),
        ),
      ),
      ended
        ? optional(
            json<{ artifacts: unknown[] }>(
              "/v1/executions/" + row.id + "/artifacts",
            ),
          )
        : undefined,
    ]);
    const last = events
      ?.filter(
        (e) =>
          e.type === "mission.logbook.entry" &&
          typeof e.payload?.message === "string",
      )
      .sort((a, b) => b.occurred_at.localeCompare(a.occurred_at))[0];
    if (last) row.activity = String(last.payload.message);
    row.evidence = files?.artifacts?.length;
  });
  const issues = deployments
    ?.filter(
      (d) =>
        d.observed_state === "failed" ||
        !!d.last_error ||
        (d.desired_state === "running" &&
          ["ready", "cancelled"].includes(d.observed_state)) ||
        (d.desired_state === "stopped" && d.observed_state === "running"),
    )
    .map((d) => ({
      ...d,
      run: runs.find((run) => run.id === d.last_execution_id),
      robot: robots.find((r) => r.id === d.robot_id)?.name ?? "Unknown robot",
      ...versions.get(d.selected_revision_id ?? ""),
    }));
  return {
    machines,
    robots,
    runs,
    recent,
    active,
    issues,
    updated: new Date(),
  };
}
