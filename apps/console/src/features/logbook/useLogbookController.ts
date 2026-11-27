import { useCallback, useEffect, useRef, useState } from "react";
import type { Interaction } from "@/features/interactions/Decisions";
import { useApiToken } from "@/hooks/useApiToken";
import { activeRun } from "@/lib/app-history";
import {
  type App,
  type AppLogbookEvent,
  type AppRun,
  type Revision,
  type Robot,
  readJson,
  readLiveRuns,
  readPage,
} from "@/lib/apps";
import { replaceQuery, studioPath, useLocation } from "@/lib/location";
import { groups } from "./logbook-model";

type Member = { user_id: string; name: string | null };
const message = (cause: unknown) =>
  cause instanceof Error ? cause.message : "History unavailable";
const optional = async <T>(value: Promise<T>) => {
  try {
    return await value;
  } catch {
    return undefined;
  }
};
/** Keeps scoped history reads, polling and cached labels out of record/list markup. */
export function useLogbookController({
  organizationId,
  runId,
}: {
  organizationId: string;
  runId?: string;
}) {
  const token = useApiToken();
  const location = useLocation();
  const params = location.searchParams;
  const appFilter = params.get("app_id") ?? params.get("app") ?? "";
  const robotFilter = params.get("robot_id") ?? params.get("robot") ?? "";
  const actorFilter = params.get("actor") ?? "";
  const stateFilter = params.get("state") ?? "";
  const days = params.get("days") ?? "";
  const from = params.get("from") ?? "";
  const until = params.get("until") ?? "";
  const group = groups.find((item) => item === params.get("group")) ?? "All";
  const tab =
    params.get("tab") === "Output" || location.hash === "#output"
      ? "Output"
      : "Overview";
  const [runs, setRuns] = useState<AppRun[]>([]);
  const [selected, setSelected] = useState<AppRun | null>(null);
  const [robots, setRobots] = useState<Robot[]>([]);
  const [apps, setApps] = useState<Record<string, App>>({});
  const [revisions, setRevisions] = useState<Record<string, Revision>>({});
  const appCache = useRef(new Map<string, App>());
  const revisionCache = useRef(new Map<string, Revision>());
  const [members, setMembers] = useState<Member[]>([]);
  const [me, setMe] = useState("");
  const [pending, setPending] = useState<string[]>([]);
  const [pendingError, setPendingError] = useState("");
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [refresh, setRefresh] = useState(0);
  const [events, setEvents] = useState<AppLogbookEvent[]>([]);
  const [eventsError, setEventsError] = useState("");
  const [eventsLoading, setEventsLoading] = useState(true);
  const current =
    selected &&
    selected.id === runId &&
    selected.organization_id === organizationId
      ? selected
      : null;
  const running = !!current && activeRun(current);
  const base = `/v1/organizations/${organizationId}`;
  const loadNames = useCallback(
    async (records: AppRun[]) => {
      await Promise.all([
        ...[...new Set(records.map((run) => run.snapshot.app_id))]
          .filter((id) => !appCache.current.has(id))
          .map(async (id) => {
            const value = await optional(
              readJson<App>(token, `/v1/apps/${id}`),
            );
            if (value) appCache.current.set(id, value);
          }),
        ...[...new Set(records.map((run) => run.snapshot.revision_id))]
          .filter((id) => !revisionCache.current.has(id))
          .map(async (id) => {
            const value = await optional(
              readJson<Revision>(token, `/v1/app-revisions/${id}`),
            );
            if (value) revisionCache.current.set(id, value);
          }),
      ]);
      return {
        apps: Object.fromEntries(appCache.current),
        revisions: Object.fromEntries(revisionCache.current),
      };
    },
    [token],
  );
  useEffect(() => {
    let live = true;
    void readPage<Robot>(token, `${base}/robots`)
      .then((items) => {
        if (live) setRobots(items);
      })
      .catch((cause) => {
        if (live) setError(message(cause));
      });
    void Promise.all([
      optional(readPage<Member>(token, `${base}/members`)),
      optional(readJson<{ user: { id: string } }>(token, "/v1/me")),
    ]).then(([people, identity]) => {
      if (live) {
        setMembers(people ?? []);
        setMe(identity?.user.id ?? "");
      }
    });
    return () => {
      live = false;
    };
  }, [base, token]);
  useEffect(() => {
    void refresh;
    if (runId) return;
    let live = true;
    let timer: number | undefined;
    const load = async () => {
      try {
        const [records, requests] = await Promise.all([
          readPage<AppRun>(token, `${base}/app-runs`),
          optional(
            readPage<Interaction>(token, `${base}/interactions?state=pending`),
          ),
        ]);
        const names = await loadNames(records);
        if (live) {
          setRuns(records);
          setApps(names.apps);
          setRevisions(names.revisions);
          setPending(requests?.map((request) => request.execution_id) ?? []);
          setPendingError(
            requests ? "" : "Pending decisions could not be checked.",
          );
          setError("");
        }
      } catch (cause) {
        if (live) setError(message(cause));
      } finally {
        if (live) {
          setLoading(false);
          timer = window.setTimeout(load, 30000);
        }
      }
    };
    void load();
    return () => {
      live = false;
      window.clearTimeout(timer);
    };
  }, [runId, base, token, loadNames, refresh]);
  useEffect(() => {
    if (runId) return;
    let live = true;
    let timer: number | undefined;
    const poll = async () => {
      try {
        const incoming = await readLiveRuns(token, `${base}/app-runs`);
        if (live)
          setRuns((rows) => {
            const merged = new Map(rows.map((run) => [run.id, run]));
            for (const run of incoming) merged.set(run.id, run);
            return [...merged.values()];
          });
      } catch {
        /* Keep the complete list while the next live read retries. */
      } finally {
        if (live)
          timer = window.setTimeout(
            poll,
            document.visibilityState === "visible" ? 2000 : 5000,
          );
      }
    };
    timer = window.setTimeout(poll, 2000);
    return () => {
      live = false;
      window.clearTimeout(timer);
    };
  }, [runId, base, token]);
  useEffect(() => {
    void refresh;
    if (!runId) return;
    let live = true;
    let shouldPoll = true;
    let timer: number | undefined;
    const load = async () => {
      try {
        const run = await readJson<AppRun>(token, `/v1/app-runs/${runId}`);
        if (run.organization_id !== organizationId) {
          shouldPoll = false;
          throw new Error("Run is unavailable in this workspace.");
        }
        shouldPoll = activeRun(run);
        if (live) {
          setSelected(run);
          setError("");
          setLoading(false);
        }
        const names = await loadNames([run]);
        if (live) {
          setApps(names.apps);
          setRevisions(names.revisions);
        }
      } catch (cause) {
        if (live) {
          setError(message(cause));
          setLoading(false);
        }
      } finally {
        if (live && shouldPoll)
          timer = window.setTimeout(
            load,
            document.visibilityState === "visible" ? 1200 : 5000,
          );
      }
    };
    void load();
    return () => {
      live = false;
      window.clearTimeout(timer);
    };
  }, [runId, organizationId, token, loadNames, refresh]);
  useEffect(() => {
    void refresh;
    if (!current?.id || tab !== "Overview") return;
    const id = current.id;
    let live = true;
    let timer: number | undefined;
    const load = async () => {
      try {
        const records = await readPage<AppLogbookEvent>(
          token,
          `${base}/app-logbook?run_id=${encodeURIComponent(id)}`,
        );
        if (live) {
          setEvents(
            records
              .filter(
                (event) =>
                  event.run_id === id &&
                  event.organization_id === organizationId,
              )
              .sort(
                (a, b) =>
                  a.occurred_at.localeCompare(b.occurred_at) ||
                  a.received_at.localeCompare(b.received_at),
              ),
          );
          setEventsError("");
        }
      } catch (cause) {
        if (live) setEventsError(message(cause));
      } finally {
        if (live) {
          setEventsLoading(false);
          if (running)
            timer = window.setTimeout(
              load,
              document.visibilityState === "visible" ? 2000 : 5000,
            );
        }
      }
    };
    void load();
    return () => {
      live = false;
      window.clearTimeout(timer);
    };
  }, [current?.id, running, base, organizationId, token, refresh, tab]);
  const person = (id: string | null) =>
    id
      ? id === me
        ? "You"
        : members.find((member) => member.user_id === id)?.name?.trim() ||
          undefined
      : undefined;
  const name = (run: AppRun) => apps[run.snapshot.app_id]?.name ?? "App";
  const release = (run: AppRun) =>
    revisions[run.snapshot.revision_id]?.manifest?.release;
  const link = (
    id: string,
    changes: Record<string, string | undefined> = {},
  ) => {
    const query = Object.fromEntries(params);
    delete query.tab;
    return studioPath("logbook", organizationId, id, { ...query, ...changes });
  };
  const update = (key: string, value: string) =>
    replaceQuery({
      [key]: value || undefined,
      ...(key === "app_id"
        ? { app: undefined }
        : key === "robot_id"
          ? { robot: undefined }
          : {}),
    });
  const filtersActive = !!(
    appFilter ||
    robotFilter ||
    actorFilter ||
    stateFilter ||
    days ||
    from ||
    until
  );
  const visible = runs
    .filter(
      (run) =>
        (!appFilter || run.snapshot.app_id === appFilter) &&
        (!robotFilter || run.robot_id === robotFilter) &&
        (!actorFilter || run.requested_by === actorFilter) &&
        (!stateFilter || run.state === stateFilter) &&
        (!days ||
          Date.parse(run.requested_at) >=
            Date.now() - Number(days) * 86400000) &&
        (!from || Date.parse(run.requested_at) >= Date.parse(from)) &&
        (!until || Date.parse(run.requested_at) <= Date.parse(until)) &&
        (group === "All" ||
          (group === "Active" && activeRun(run)) ||
          (group === "Finished" && !activeRun(run)) ||
          (group === "Needs attention" &&
            (["failed", "interrupted"].includes(run.state) ||
              pending.includes(run.id)))),
    )
    .sort((a, b) => b.requested_at.localeCompare(a.requested_at));

  return {
    token,
    organizationId,
    runId,
    current,
    running,
    robots,
    runs,
    apps,
    revisions,
    pending,
    pendingError,
    error,
    loading,
    refresh,
    events,
    eventsError,
    eventsLoading,
    name,
    person,
    release,
    link,
    update,
    filtersActive,
    visible,
    group,
    tab,
    appFilter,
    robotFilter,
    actorFilter,
    stateFilter,
    days,
    from,
    until,
    reload: () => setRefresh((value) => value + 1),
  };
}
export type LogbookModel = ReturnType<typeof useLogbookController>;
