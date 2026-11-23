import { useAuth0 } from "@auth0/auth0-react";
import { Fragment, useCallback, useEffect, useMemo, useState } from "react";
import {
  type Artifact,
  ArtifactCard,
  ArtifactGallery,
} from "@/components/ArtifactGallery";
import { apiFetch, apiFetchAll } from "@/lib/api";
import { resourcePath, setQuery, useLocation, visit } from "@/lib/location";
import { RunTimeline } from "./RunTimeline";
import { StudioTime } from "./StudioTime";
import { Decisions } from "./Decisions";

type Organization = { id: string; name: string; role?: string };
type Mission = { id: string; name: string };
type Robot = { id: string; name: string };
type Member = {
  name?: string;
  user_id: string;
  role: "owner" | "developer" | "operator";
};
type Revision = { id: string; version: number };
type Execution = {
  updated_at?: string;
  id: string;
  mission_revision_id: string | null;
  actor_user_id: string | null;
  state: string;
  created_at: string;
};
type Run = Execution & {
  robot: Robot;
  mission: Mission | null;
  version?: number;
};
type Entry = {
  event_id: string;
  robot_id: string;
  local_sequence: number | null;
  operation_id: string | null;
  execution_id: string | null;
  actor_user_id: string | null;
  type: string;
  occurred_at: string;
  received_at: string;
  payload: Record<string, unknown>;
};
type OutputChunk = {
  data_base64: string;
  next_offset: number;
  truncated: boolean;
};
type Output = { stdout: string; stderr: string; truncated: boolean };

const active = (state: string) =>
  ["running", "queued", "preparing", "starting", "stopping"].includes(state);
const finished = (state: string) =>
  ["succeeded", "failed", "cancelled"].includes(state);
const DIRECT = "__direct__";
const UNKNOWN_MISSION = "__unknown_mission__";
const UNKNOWN_USER = "__unknown_user__";
const message = (error: unknown) =>
  error instanceof Error ? error.message : "Request failed";
const timestamp = (date: string) => new Date(date).toLocaleString();
const missionKey = (run: Run) =>
  run.mission?.id ?? (run.mission_revision_id ? UNKNOWN_MISSION : DIRECT);
const missionName = (run: Run) =>
  run.mission?.name ??
  (run.mission_revision_id ? "Unknown mission" : "Direct run");
const entryTitle = (entry: Entry) =>
  typeof entry.payload.message === "string"
    ? entry.payload.message
    : typeof entry.payload.event_type === "string"
      ? entry.payload.event_type
      : entry.type.startsWith("mission.") &&
          typeof entry.payload.state === "string"
        ? `Mission ${entry.payload.state.replaceAll("_", " ")}`
        : entry.type;
const evidenceIds = (entry: Entry) =>
  Array.isArray(entry.payload.evidence)
    ? entry.payload.evidence.filter(
        (value): value is string => typeof value === "string",
      )
    : [];
const humanizeChoice = (value: string) =>
  value
    .replaceAll("_", " ")
    .replaceAll("-", " ")
    .replace(/^./, (letter) => letter.toUpperCase());

type HitlSelection = {
  key: string;
  label: string;
  value: string;
};
type HitlDecision = {
  selections: HitlSelection[];
  state?: string;
  source?: string;
};

function hitlDecision(entry: Entry): HitlDecision | null {
  if (entry.type === "human.interaction.answered") {
    if (
      typeof entry.payload.outcome === "string" &&
      typeof entry.payload.selected_label === "string"
    ) {
      return {
        selections: [
          {
            key: "Operator response",
            label: entry.payload.selected_label,
            value: entry.payload.outcome,
          },
        ],
      };
    }
    return null;
  }
  if (entry.type !== "mission.logbook.entry") return null;
  const fields = entry.payload.fields;
  if (!fields || typeof fields !== "object" || Array.isArray(fields))
    return null;
  const values = fields as Record<string, unknown>;
  if (
    entry.payload.message === "Human interaction resolved" &&
    typeof values.outcome === "string"
  ) {
    return {
      selections: [
        {
          key:
            typeof values.question === "string"
              ? humanizeChoice(values.question)
              : "Decision",
          label:
            typeof values.selected_label === "string"
              ? values.selected_label
              : humanizeChoice(values.outcome),
          value: values.outcome,
        },
      ],
      state: typeof values.state === "string" ? values.state : undefined,
      source: typeof values.source === "string" ? values.source : undefined,
    };
  }
  if (entry.payload.message === "HITL demonstration completed") {
    const selections = ["target", "detail", "approval"].flatMap((key) =>
      typeof values[key] === "string"
        ? [
            {
              key: humanizeChoice(key),
              label: humanizeChoice(values[key]),
              value: values[key],
            },
          ]
        : [],
    );
    return selections.length ? { selections } : null;
  }
  return null;
}

function HitlDecisionPanel({ entry }: { entry: Entry }) {
  const decision = hitlDecision(entry);
  if (!decision) return null;
  return (
    <section className="logbook2-hitl-decision" aria-label="Selected options">
      <header>
        <span>
          Selected option{decision.selections.length === 1 ? "" : "s"}
        </span>
        {(decision.state || decision.source) && (
          <small>
            {[decision.state, decision.source].filter(Boolean).join(" · ")}
          </small>
        )}
      </header>
      <div className="logbook2-hitl-selections">
        {decision.selections.map((selection) => (
          <div key={selection.key}>
            <span>{selection.key}</span>
            <strong>{selection.label}</strong>
            <code>{selection.value}</code>
          </div>
        ))}
      </div>
    </section>
  );
}

function eventTone(entry: Entry) {
  const outcome =
    entry.type === "agent.motion.event"
      ? entry.payload.event_type
      : entry.type === "device.operation.state_changed"
        ? entry.payload.state
        : entry.type.startsWith("mission.")
          ? entry.payload.state
          : null;
  if (
    [
      "motion.failed",
      "motion.refused",
      "motion.guard_tripped",
      "failed",
    ].includes(String(outcome))
  )
    return "danger";
  if (
    ["motion.completed", "motion.stop_verified", "succeeded"].includes(
      String(outcome),
    )
  )
    return "success";
  if (["motion.accepted", "accepted", "running"].includes(String(outcome)))
    return "active";
  if (entry.type === "mission.logbook.entry") return "note";
  return "neutral";
}

async function readOutput(
  getToken: () => Promise<string>,
  executionId: string,
  stream: string,
) {
  let offset = 0;
  let truncated = false;
  let text = "";
  const decoder = new TextDecoder();
  do {
    const chunk = (await apiFetch(
      getToken,
      `/v1/executions/${encodeURIComponent(executionId)}/output?stream=${stream}&offset=${offset}&limit=16384`,
    ).then((response) => response.json())) as OutputChunk;
    const bytes = Uint8Array.from(atob(chunk.data_base64), (char) =>
      char.charCodeAt(0),
    );
    text += decoder.decode(bytes, { stream: true });
    truncated ||= chunk.truncated;
    if (chunk.next_offset <= offset || chunk.next_offset - offset < 16384)
      break;
    offset = chunk.next_offset;
    if (offset >= 65536) truncated = true;
  } while (offset < 65536);
  return { text: text + decoder.decode(), truncated };
}

function FilterField({
  label,
  value,
  onChange,
  children,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  children: React.ReactNode;
}) {
  return (
    <label className="logbook2-filter">
      <span>{label}</span>
      <select value={value} onChange={(event) => onChange(event.target.value)}>
        {children}
      </select>
    </label>
  );
}

export function LogbookPage({
  preferredOrganizationId,
  pollIntervalMs = 3000,
  timelineOnly = false,
  routeId,
  selectRecord,
}: {
  preferredOrganizationId: string;
  onOrganizationChange: (id: string) => void;
  pollIntervalMs?: number;
  timelineOnly?: boolean;
  routeId?: string;
  selectRecord?: (id: string, org: string) => void;
}) {
  const { getAccessTokenSilently } = useAuth0();
  const getToken = useCallback(async () => {
    const token = await getAccessTokenSilently({
      authorizationParams: { audience: import.meta.env.VITE_AUTH0_AUDIENCE },
    });
    if (!token) throw new Error("No access token available");
    return token;
  }, [getAccessTokenSilently]);
  const [orgs, setOrgs] = useState<Organization[] | null>(null);
  const [orgId, setOrgId] = useState(preferredOrganizationId);
  const [me, setMe] = useState("");
  const [missions, setMissions] = useState<Mission[]>([]);
  const [robots, setRobots] = useState<Robot[]>([]);
  const [members, setMembers] = useState<Member[]>([]);
  const [pendingIds, setPendingIds] = useState<string[]>([]);
  const [runs, setRuns] = useState<Run[] | null>(null);
  const url = useLocation();
  const days = url.searchParams.get("days") ?? "";
  const group = url.searchParams.get("group") ?? "All";
  const tab = url.searchParams.get("tab") ?? "Overview";
  const missionFilter = url.searchParams.get("app") ?? "";
  const robotFilter = url.searchParams.get("robot") ?? "";
  const humanFilter = url.searchParams.get("actor") ?? "";
  const stateFilter = url.searchParams.get("state") ?? "";
  const [localSelection, setLocalSelection] = useState("");
  const selection = routeId ?? localSelection;
  const setSelection = (id: string) =>
    selectRecord ? selectRecord(id, orgId) : setLocalSelection(id);
  const [entries, setEntries] = useState<Entry[] | null>(null);
  const [output, setOutput] = useState<Output | null>(null);
  const [artifacts, setArtifacts] = useState<Artifact[] | null>(null);
  const [artifactError, setArtifactError] = useState("");
  const [error, setError] = useState("");
  const [entriesError, setEntriesError] = useState("");
  const [outputError, setOutputError] = useState("");
  const [refresh, setRefresh] = useState(0);
  const [follow, setFollow] = useState(true);
  const [copyError, setCopyError] = useState("");
  const [detailRefresh, setDetailRefresh] = useState(0);
  const [uiPanelRun, setUiPanelRun] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    apiFetchAll<Organization>(getToken, "/v1/organizations")
      .then((items) => {
        if (cancelled) return;
        setOrgs(items);
        setOrgId((current) =>
          items.some((item) => item.id === current)
            ? current
            : (items[0]?.id ?? ""),
        );
      })
      .catch((cause) => {
        if (!cancelled) setError(message(cause));
      });
    apiFetch(getToken, "/v1/me")
      .then((response) => response.json())
      .then((data: { user: { id: string } }) => {
        if (!cancelled) setMe(data.user.id);
      })
      .catch(() => {});
    return () => {
      cancelled = true;
    };
  }, [getToken]);

  useEffect(() => {
    if (!routeId || preferredOrganizationId) return;
    let current = true;
    async function resolveWorkspace() {
      const run = await (
        await apiFetch(
          getToken,
          "/v1/executions/" + encodeURIComponent(routeId!),
        )
      ).json();
      const robot = await (
        await apiFetch(
          getToken,
          "/v1/robots/" + encodeURIComponent(run.robot_id),
        )
      ).json();
      if (current && robot.organization_id) setOrgId(robot.organization_id);
    }
    void resolveWorkspace().catch((cause) => {
      if (current) setError(message(cause));
    });
    return () => {
      current = false;
    };
  }, [routeId, preferredOrganizationId, getToken]);

  // biome-ignore lint/correctness/useExhaustiveDependencies: refresh requests a new snapshot
  useEffect(() => {
    if (!orgId) return;
    let cancelled = false;
    setError("");

    void apiFetchAll<{ execution_id: string }>(
      getToken,
      `/v1/organizations/${orgId}/interactions?state=pending`,
    )
      .then((items) => {
        if (!cancelled) setPendingIds(items.map((item) => item.execution_id));
      })
      .catch(() => {});
    Promise.all([
      apiFetchAll<Mission>(getToken, `/v1/organizations/${orgId}/missions`),
      apiFetchAll<Robot>(getToken, `/v1/organizations/${orgId}/robots`),
      apiFetchAll<Member>(getToken, `/v1/organizations/${orgId}/members`),
    ])
      .then(async ([missionList, robotList, memberList]) => {
        const [revisionLists, executionLists] = await Promise.all([
          Promise.all(
            missionList.map((mission) =>
              apiFetchAll<Revision>(
                getToken,
                `/v1/missions/${mission.id}/revisions`,
              ),
            ),
          ),
          Promise.all(
            robotList.map((robot) =>
              apiFetchAll<Execution>(
                getToken,
                `/v1/robots/${robot.id}/executions`,
              ),
            ),
          ),
        ]);
        if (cancelled) return;
        const revisionMap = new Map<
          string,
          { mission: Mission; version: number }
        >();
        missionList.forEach((mission, index) => {
          revisionLists[index].forEach((revision) => {
            revisionMap.set(revision.id, {
              mission,
              version: revision.version,
            });
          });
        });
        const allRuns = robotList.flatMap((robot, index) =>
          executionLists[index].map((run) => {
            const revision = run.mission_revision_id
              ? revisionMap.get(run.mission_revision_id)
              : undefined;
            return {
              ...run,
              robot,
              mission: revision?.mission ?? null,
              version: revision?.version,
            };
          }),
        );
        allRuns.sort((left, right) =>
          right.created_at.localeCompare(left.created_at),
        );
        setMissions(missionList);
        setRobots(robotList);
        setMembers(memberList);
        setRuns(allRuns);
      })
      .catch((cause) => {
        if (!cancelled) setError(message(cause));
      });
    return () => {
      cancelled = true;
    };
  }, [orgId, getToken, refresh]);

  const rolesByUser = useMemo(
    () => new Map(members.map((member) => [member.user_id, member.role])),
    [members],
  );
  const visibleRuns = useMemo(
    () =>
      (runs ?? []).filter(
        (run) =>
          (group === "All" ||
            (group === "Active" && active(run.state)) ||
            (group === "Finished" && finished(run.state)) ||
            (group === "Needs attention" &&
              (["failed", "unknown"].includes(run.state) ||
                pendingIds.includes(run.id)))) &&
          (!days ||
            Date.parse(run.created_at) >=
              Date.now() - Number(days) * 86400000) &&
          (!missionFilter || missionKey(run) === missionFilter) &&
          (!robotFilter || run.robot.id === robotFilter) &&
          (!humanFilter ||
            (run.actor_user_id ?? UNKNOWN_USER) === humanFilter) &&
          (!stateFilter || run.state === stateFilter),
      ),
    [
      runs,
      days,
      pendingIds,
      group,
      missionFilter,
      robotFilter,
      humanFilter,
      stateFilter,
    ],
  );
  const selectedRun = runs?.find((run) => run.id === selection);
  useEffect(() => {
    if (!selectedRun || !active(selectedRun.state)) return;
    let current = true;
    const update = async () => {
      try {
        const snapshot: Execution = await (
          await apiFetch(
            getToken,
            "/v1/executions/" + encodeURIComponent(selectedRun.id),
          )
        ).json();
        if (current && snapshot.id === selectedRun.id) {
          setError("");
          if (snapshot.state !== selectedRun.state)
            setRuns(
              (rows) =>
                rows?.map((row) =>
                  row.id === snapshot.id ? { ...row, ...snapshot } : row,
                ) ?? null,
            );
        }
      } catch {
        if (current)
          setError("Connection lost. Showing the last known run state.");
      }
    };
    const timer = window.setInterval(() => void update(), 5000);
    return () => {
      current = false;
      window.clearInterval(timer);
    };
  }, [getToken, selectedRun]);
  const humans = useMemo(
    () =>
      [
        ...new Set(
          (runs ?? []).map((run) => run.actor_user_id ?? UNKNOWN_USER),
        ),
      ].sort(),
    [runs],
  );
  const states = useMemo(
    () => [...new Set((runs ?? []).map((run) => run.state))].sort(),
    [runs],
  );
  const filtersActive = !!(
    days ||
    missionFilter ||
    robotFilter ||
    humanFilter ||
    stateFilter
  );
  const linkedEvidence = new Set(
    (entries ?? []).flatMap((entry) => evidenceIds(entry)),
  );
  const unlinkedImages = (artifacts ?? []).filter(
    (artifact) =>
      !linkedEvidence.has(artifact.artifact_id) &&
      (artifact.content_type === "image/jpeg" ||
        artifact.content_type === "image/png"),
  );

  // biome-ignore lint/correctness/useExhaustiveDependencies: changing runs resets the displayed detail snapshot.
  useEffect(() => {
    setEntries(null);
    setOutput(null);
    setArtifacts(null);
    setFollow(true);
    setCopyError("");
  }, [selection]);
  useEffect(() => {
    if (!selectedRun || !active(selectedRun.state)) return;
    const timer = window.setInterval(() => {
      if (tab !== "Output" || follow) setDetailRefresh((value) => value + 1);
    }, 5000);
    return () => window.clearInterval(timer);
  }, [selectedRun, tab, follow]);
  // biome-ignore lint/correctness/useExhaustiveDependencies: detailRefresh requests a new snapshot
  useEffect(() => {
    setEntriesError("");
    setOutputError("");
    if (!orgId || !selectedRun) return;
    let cancelled = false;
    const params = new URLSearchParams({
      robot_id: selectedRun.robot.id,
      execution_id: selectedRun.id,
    });
    apiFetchAll<Entry>(getToken, `/v1/organizations/${orgId}/events?${params}`)
      .then((eventList) => {
        if (cancelled) return;
        setEntries(
          eventList
            .slice()
            .sort(
              (left, right) =>
                left.occurred_at.localeCompare(right.occurred_at) ||
                (left.local_sequence ?? 0) - (right.local_sequence ?? 0),
            ),
        );
      })
      .catch((cause) => {
        if (!cancelled) setEntriesError(message(cause));
      });
    Promise.all([
      readOutput(getToken, selectedRun.id, "stdout"),
      readOutput(getToken, selectedRun.id, "stderr"),
    ])
      .then(([stdout, stderr]) => {
        if (cancelled) return;
        setOutput({
          stdout: stdout.text,
          stderr: stderr.text,
          truncated: stdout.truncated || stderr.truncated,
        });
      })
      .catch((cause) => {
        if (!cancelled) setOutputError(message(cause));
      });
    return () => {
      cancelled = true;
    };
  }, [orgId, selectedRun, getToken, detailRefresh]);

  useEffect(() => {
    setArtifacts(null);
    setArtifactError("");
    if (!selectedRun) return;
    let cancelled = false;
    let loading = false;
    const load = async () => {
      if (loading) return;
      loading = true;
      try {
        const body = (await apiFetch(
          getToken,
          `/v1/executions/${encodeURIComponent(selectedRun.id)}/artifacts`,
        ).then((response) => response.json())) as { artifacts: Artifact[] };
        if (!cancelled) {
          setArtifacts(body.artifacts);
          setArtifactError("");
        }
      } catch (cause) {
        if (!cancelled) setArtifactError(message(cause));
      } finally {
        loading = false;
      }
    };
    void load();
    const timer = finished(selectedRun.state)
      ? undefined
      : window.setInterval(() => void load(), pollIntervalMs);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [getToken, pollIntervalMs, selectedRun]);

  useEffect(() => {
    if (!orgId || selection) return;
    const timer = window.setInterval(
      () => setRefresh((value) => value + 1),
      30000,
    );
    return () => window.clearInterval(timer);
  }, [orgId, selection]);
  const clearFilters = () => {
    for (const name of ["app", "robot", "actor", "state", "days"])
      url.searchParams.delete(name);
    visit(url.pathname + url.search);
  };
  const humanName = (id: string | null, viewer: string) =>
    id && id === viewer
      ? "You"
      : members.find((m) => m.user_id === id)?.name || "Name unavailable";
  const backToRuns = () => {
    const from = window.history.state?.fromList;
    if (
      typeof from === "string" &&
      new URL(from, window.location.origin).pathname === "/runs"
    ) {
      const top = window.history.state.listScroll;
      visit(from);
      window.history.replaceState(
        { ...window.history.state, runListScroll: top },
        "",
      );
    } else setSelection("");
  };
  const openRun = (id: string) => {
    window.history.replaceState(
      { ...window.history.state, runListScroll: window.scrollY },
      "",
    );
    setSelection(id);
    window.scrollTo({ top: 0, behavior: "auto" });
  };

  useEffect(() => {
    if (
      selection ||
      !runs ||
      typeof window.history.state?.runListScroll !== "number"
    )
      return;
    window.scrollTo({
      top: window.history.state.runListScroll,
      behavior: "instant",
    });
  }, [selection, runs]);

  if (selection && !selectedRun)
    return (
      <main className="workspace-page">
        <h1>Run</h1>
        <p>
          {error ||
            (runs ? "Run unavailable in this workspace." : "Loading run…")}
        </p>
        <button type="button" onClick={backToRuns}>
          Back to runs
        </button>
      </main>
    );
  if (selectedRun) {
    const clip = {
      ...selectedRun,
      app: missionName(selectedRun),
      person: humanName(selectedRun.actor_user_id, me),
    };
    return (
      <main className="logbook2-page logbook2-detail">
        <header className="logbook2-detail-toolbar">
          <button className="logbook2-back" type="button" onClick={backToRuns}>
            <span aria-hidden="true">←</span> Back to runs
          </button>
          <button
            className="button-secondary"
            type="button"
            onClick={() => setDetailRefresh((value) => value + 1)}
          >
            Refresh
          </button>
        </header>
        {error && (
          <p role="alert" className="notice-error">
            {error}
          </p>
        )}
        {active(selectedRun.state) && !error && (
          <Decisions
            key={selectedRun.id}
            executionId={selectedRun.id}
            organizationId={orgId}
            robots={robots}
            getToken={getToken}
            refresh={detailRefresh}
            role={orgs?.find((o) => o.id === orgId)?.role}
          />
        )}
        <section className="logbook2-record-header">
          <div className="logbook2-record-heading">
            <div>
              <span className="logbook2-kicker">Execution record</span>
              <h1>
                {selectedRun.mission ? (
                  <a href={resourcePath("apps", selectedRun.mission.id, orgId)}>
                    {missionName(selectedRun)}
                  </a>
                ) : (
                  missionName(selectedRun)
                )}
              </h1>
            </div>
            <span className="logbook2-state" data-state={selectedRun.state}>
              {selectedRun.state === "succeeded"
                ? "Execution succeeded"
                : selectedRun.state}
            </span>
          </div>
          <dl className="logbook2-record-meta">
            <div>
              <dt>Started</dt>
              <dd>
                <StudioTime value={selectedRun.created_at} />
              </dd>
            </div>
            {selectedRun.updated_at && (
              <div>
                <dt>Last reported update</dt>
                <dd>
                  <time
                    dateTime={selectedRun.updated_at}
                    title={selectedRun.updated_at}
                  >
                    {timestamp(selectedRun.updated_at)}
                  </time>
                </dd>
              </div>
            )}
            <div>
              <dt>Robot</dt>
              <dd>
                <a href={resourcePath("robots", selectedRun.robot.id, orgId)}>
                  {selectedRun.robot.name}
                </a>
              </dd>
            </div>
            <div>
              <dt>Initiated by</dt>
              <dd>{humanName(selectedRun.actor_user_id, me)}</dd>
            </div>
            <div>
              <dt>Role</dt>
              <dd>
                {selectedRun.actor_user_id
                  ? (rolesByUser.get(selectedRun.actor_user_id) ?? "unknown")
                  : "unknown"}
              </dd>
            </div>
            <div>
              <dt>Revision</dt>
              <dd>
                {selectedRun.version === undefined
                  ? "Not linked"
                  : `Version ${selectedRun.version}`}
              </dd>
            </div>
          </dl>
          <details>
            <summary>Technical details</summary>Execution ID · {selectedRun.id}
          </details>
        </section>

        <nav className="route-tabs" aria-label="Run sections">
          {["Overview", "App UI", "Output"].map((label) => (
            <button
              type="button"
              key={label}
              aria-current={tab === label ? "page" : undefined}
              onClick={() => setQuery("tab", label)}
            >
              {label}
            </button>
          ))}
        </nav>
        {tab === "App UI" && (
          <p className="empty-panel">
            {finished(selectedRun.state)
              ? "This run has ended."
              : "No interface address has been reported for this run."}
          </p>
        )}
        {["Overview", "Logbook"].includes(tab) && (
          <section className="empty-panel">
            <h2>Current situation</h2>
            <p>
              {entries?.length
                ? entryTitle(entries[entries.length - 1])
                : selectedRun.state}
            </p>
          </section>
        )}
        {["Overview", "Logbook"].includes(tab) && (
          <RunTimeline
            key={clip.id}
            preview
            focusedRun={clip}
            robots={[clip.robot]}
            runs={[clip]}
            workspace={orgId}
            token={getToken}
            robotFilter={clip.robot.id}
          />
        )}
        <section
          hidden={!["Overview", "Logbook"].includes(tab)}
          className="logbook2-section"
          aria-label="Logbook timeline"
        >
          <header className="logbook2-section-heading">
            <div>
              <span className="logbook2-section-index">01</span>
              <h2>Timeline</h2>
            </div>
            <span>
              {entries === null ? "—" : entries.length + unlinkedImages.length}{" "}
              events
            </span>
          </header>
          {entriesError ? (
            <p role="alert" className="notice-error logbook2-detail-error">
              Timeline unavailable: {entriesError}
            </p>
          ) : entries === null ? (
            <p className="logbook2-empty">Loading events…</p>
          ) : (
            <div className="logbook2-timeline">
              {entries.length === 0 && unlinkedImages.length === 0 && (
                <p className="logbook2-empty">No events recorded.</p>
              )}
              {entries.map((entry) => (
                <details
                  className="logbook2-event"
                  data-tone={eventTone(entry)}
                  key={entry.event_id}
                >
                  <summary>
                    <span className="logbook2-event-marker" />
                    <span className="logbook2-event-copy">
                      <strong>{entryTitle(entry)}</strong>
                      <small>
                        {entry.type}
                        {hitlDecision(entry)?.selections[0] &&
                          ` · Selected: ${hitlDecision(entry)?.selections[0].label}`}
                        {evidenceIds(entry).length > 0 &&
                          ` · ${evidenceIds(entry).length} evidence file${evidenceIds(entry).length === 1 ? "" : "s"}`}
                      </small>
                    </span>
                    <StudioTime value={entry.occurred_at} />
                    <span className="logbook2-event-open" aria-hidden="true">
                      +
                    </span>
                  </summary>
                  <div className="logbook2-event-detail">
                    <dl>
                      <dt>Sequence</dt>
                      <dd>{entry.local_sequence ?? "Hosted event"}</dd>
                      <dt>Actor</dt>
                      <dd>{humanName(entry.actor_user_id, me)}</dd>
                      <dt>Operation</dt>
                      <dd>{entry.operation_id ?? "—"}</dd>
                      <dt>Received</dt>
                      <dd>
                        <StudioTime value={entry.received_at} />
                      </dd>
                    </dl>
                    <HitlDecisionPanel entry={entry} />
                    <details>
                      <summary>Technical details</summary>Actor ID ·{" "}
                      <code>{entry.actor_user_id ?? "Unavailable"}</code>
                      <pre>{JSON.stringify(entry.payload, null, 2)}</pre>
                    </details>
                    {evidenceIds(entry).length > 0 && (
                      <section
                        className="logbook2-evidence-links logbook2-event-artifacts"
                        aria-label="Event evidence"
                      >
                        <strong>Evidence</strong>
                        {evidenceIds(entry).map((id) => {
                          const artifact = artifacts?.find(
                            (item) => item.artifact_id === id,
                          );
                          if (
                            artifact?.state === "available" &&
                            (artifact.content_type === "image/jpeg" ||
                              artifact.content_type === "image/png")
                          ) {
                            return (
                              <ArtifactCard
                                key={id}
                                artifact={artifact}
                                executionId={selectedRun.id}
                                getToken={getToken}
                                compact
                              />
                            );
                          }
                          return artifact ? (
                            <a
                              key={id}
                              href={`#artifact-${encodeURIComponent(id)}`}
                            >
                              {artifact.path.split("/").at(-1)} ·{" "}
                              {artifact.state}
                            </a>
                          ) : (
                            <span
                              className="logbook2-evidence-pending"
                              key={id}
                            >
                              Pending · {id}
                            </span>
                          );
                        })}
                      </section>
                    )}
                  </div>
                </details>
              ))}
              {unlinkedImages.map((artifact) => (
                <details
                  className="logbook2-event logbook2-artifact-event"
                  data-tone="note"
                  key={`artifact-${artifact.artifact_id}`}
                  open
                >
                  <summary>
                    <span className="logbook2-event-marker" />
                    <span className="logbook2-event-copy">
                      <strong>
                        Photo evidence · {artifact.path.split("/").at(-1)}
                      </strong>
                      <small>Mission artifact · {artifact.state}</small>
                    </span>
                  </summary>
                  <div className="logbook2-event-detail">
                    <ArtifactCard
                      artifact={artifact}
                      executionId={selectedRun.id}
                      getToken={getToken}
                      compact
                    />
                  </div>
                </details>
              ))}
            </div>
          )}
        </section>
        <div hidden={!["Overview", "Logbook"].includes(tab)}>
          <ArtifactGallery
            artifacts={artifacts}
            executionId={selectedRun.id}
            getToken={getToken}
            error={artifactError}
            pollIntervalMs={finished(selectedRun.state) ? 0 : pollIntervalMs}
            sectionIndex="02"
          />
        </div>
        <section
          hidden={tab !== "Output"}
          className="logbook2-section"
          aria-label="Execution output"
        >
          <header className="logbook2-section-heading">
            <div>
              <span className="logbook2-section-index">03</span>
              <h2>Execution output</h2>
            </div>
            {output?.truncated && <span>Truncated</span>}
          </header>
          <div className="robot-actions output-actions">
            {active(selectedRun.state) && (
              <button
                className="button-secondary"
                type="button"
                onClick={() => setFollow(!follow)}
              >
                {follow ? "Pause updates" : "Resume updates"}
              </button>
            )}
            {output &&
              ["stdout", "stderr"].map((stream) => (
                <span key={stream}>
                  <button
                    className="text-action"
                    type="button"
                    onClick={() =>
                      void navigator.clipboard
                        .writeText(output[stream as "stdout" | "stderr"])
                        .catch(() => setCopyError("Clipboard unavailable."))
                    }
                  >
                    Copy {stream}
                  </button>
                  {" · "}
                  <button
                    className="text-action"
                    type="button"
                    onClick={() => {
                      const href = URL.createObjectURL(
                        new Blob([output[stream as "stdout" | "stderr"]], {
                          type: "text/plain",
                        }),
                      );
                      const link = document.createElement("a");
                      link.href = href;
                      link.download = selectedRun.id + "-" + stream + ".txt";
                      link.click();
                      setTimeout(() => URL.revokeObjectURL(href), 1000);
                    }}
                  >
                    Download
                  </button>
                </span>
              ))}
          </div>
          {copyError && <p role="alert">{copyError}</p>}
          {outputError ? (
            <p role="alert" className="notice-error logbook2-detail-error">
              Output unavailable: {outputError}
            </p>
          ) : output === null ? (
            <p className="logbook2-empty">Loading output…</p>
          ) : (
            <div className="logbook2-output-grid">
              <div>
                <h3>Standard output</h3>
                <pre>{output.stdout || "No output."}</pre>
              </div>
              <div>
                <h3>Standard error</h3>
                <pre>{output.stderr || "No errors."}</pre>
              </div>
            </div>
          )}
        </section>
      </main>
    );
  }

  return (
    <main className="logbook2-page">
      <header className="logbook2-page-header">
        <div>
          <h1>{timelineOnly ? "Timeline" : "Runs"}</h1>
          <p>
            {timelineOnly
              ? "Robot tracks and app runs in one time window."
              : "Execution history for this workspace."}
          </p>
        </div>
        <button
          className="button-secondary"
          type="button"
          onClick={() => setRefresh((value) => value + 1)}
        >
          Refresh
        </button>
      </header>

      {!timelineOnly && runs && (
        <p className="studio-active-summary">
          <strong>Active now</strong> ·{" "}
          {runs.filter((run) => active(run.state)).length} active runs
        </p>
      )}
      {!timelineOnly && (
        <nav className="route-tabs" aria-label="Run groups">
          {["Active", "Needs attention", "Finished", "All"].map((label) => (
            <button
              type="button"
              key={label}
              aria-current={group === label ? "page" : undefined}
              onClick={() => setQuery("group", label)}
            >
              {label}
            </button>
          ))}
        </nav>
      )}
      <details className="logbook2-filters">
        <summary>Filter runs{filtersActive ? " · Applied" : ""}</summary>
        <div className="logbook2-filter-heading">
          <span>Filter executions</span>
          <button
            type="button"
            onClick={clearFilters}
            disabled={!filtersActive}
          >
            Clear all
          </button>
        </div>
        <div className="logbook2-filter-grid">
          <FilterField
            label="Started within"
            value={days}
            onChange={(value) => setQuery("days", value)}
          >
            <option value="">Any time</option>
            <option value="1">Last day</option>
            <option value="7">Last week</option>
            <option value="30">Last 30 days</option>
          </FilterField>
          <FilterField
            label="App"
            value={missionFilter}
            onChange={(value) => setQuery("app", value)}
          >
            <option value="">All apps</option>
            {missions.map((mission) => (
              <option key={mission.id} value={mission.id}>
                {mission.name}
              </option>
            ))}
            {runs?.some((run) => missionKey(run) === DIRECT) && (
              <option value={DIRECT}>Direct runs</option>
            )}
            {runs?.some((run) => missionKey(run) === UNKNOWN_MISSION) && (
              <option value={UNKNOWN_MISSION}>Unknown mission</option>
            )}
          </FilterField>
          <FilterField
            label="Robot"
            value={robotFilter}
            onChange={(value) => setQuery("robot", value)}
          >
            <option value="">All robots</option>
            {robots.map((robot) => (
              <option key={robot.id} value={robot.id}>
                {robot.name}
              </option>
            ))}
          </FilterField>
          <FilterField
            label="Initiated by"
            value={humanFilter}
            onChange={(value) => setQuery("actor", value)}
          >
            <option value="">All humans</option>
            {humans.map((id) => (
              <option key={id} value={id}>
                {humanName(id === UNKNOWN_USER ? null : id, me)}
              </option>
            ))}
          </FilterField>
          <FilterField
            label="Status"
            value={stateFilter}
            onChange={(value) => setQuery("state", value)}
          >
            <option value="">All statuses</option>
            {states.map((state) => (
              <option key={state} value={state}>
                {state}
              </option>
            ))}
          </FilterField>
        </div>
      </details>

      {timelineOnly && runs && !error ? (
        <RunTimeline
          robots={robots.filter(
            (robot) => !robotFilter || robot.id === robotFilter,
          )}
          runs={visibleRuns.map((run) => ({
            ...run,
            app: missionName(run),
            person: humanName(run.actor_user_id, me),
          }))}
          token={getToken}
          workspace={orgId}
        />
      ) : (
        <section className="logbook2-table-panel" aria-label="Executions">
          <header>
            <h2>Executions</h2>
            <span>{runs ? `${visibleRuns.length} records` : "Loading"}</span>
          </header>
          {error ? (
            <p role="alert" className="notice-error logbook2-empty">
              {error}
            </p>
          ) : orgs?.length === 0 ? (
            <p className="logbook2-empty">No workspace available.</p>
          ) : runs === null ? (
            <p className="logbook2-empty">Loading executions…</p>
          ) : visibleRuns.length === 0 ? (
            <p className="logbook2-empty">No executions match these filters.</p>
          ) : (
            <div className="logbook2-table-scroll">
              <table className="logbook2-table">
                <thead>
                  <tr>
                    <th>Run</th>
                    <th>Timing and status</th>
                    <th>Details</th>
                  </tr>
                </thead>
                <tbody>
                  {visibleRuns.map((run) => (
                    <Fragment key={run.id}>
                      <tr>
                        <td>
                          {run.mission ? (
                            <a
                              className="studio-app"
                              href={resourcePath("apps", run.mission.id, orgId)}
                            >
                              {missionName(run)}
                            </a>
                          ) : (
                            <strong>{missionName(run)}</strong>
                          )}
                          <small>
                            {run.version === undefined
                              ? "Unversioned"
                              : `Version ${run.version}`}{" "}
                            ·{" "}
                            <a
                              className="studio-robot-link"
                              href={resourcePath("robots", run.robot.id, orgId)}
                            >
                              {run.robot.name}
                            </a>{" "}
                            · Started by {humanName(run.actor_user_id, me)}
                          </small>
                        </td>
                        <td>
                          <StudioTime value={run.created_at} />
                          <small>
                            <span
                              className="logbook2-state"
                              data-state={run.state}
                            >
                              {run.state}
                            </span>
                          </small>
                        </td>
                        <td className="logbook2-open">
                          {active(run.state) && (
                            <button
                              type="button"
                              className="text-action"
                              aria-expanded={uiPanelRun === run.id}
                              onClick={() =>
                                setUiPanelRun(
                                  uiPanelRun === run.id ? null : run.id,
                                )
                              }
                            >
                              View UI
                            </button>
                          )}
                          <a
                            href={resourcePath("runs", run.id, orgId)}
                            aria-label={`Open ${missionName(run)} execution from ${timestamp(run.created_at)}`}
                            onClick={(e) => {
                              if (
                                !e.ctrlKey &&
                                !e.metaKey &&
                                !e.shiftKey &&
                                !e.altKey
                              ) {
                                e.preventDefault();
                                openRun(run.id);
                              }
                            }}
                          >
                            →
                          </a>
                        </td>
                      </tr>
                      {uiPanelRun === run.id && (
                        <tr key={run.id + "-ui"} className="run-ui-panel-row">
                          <td colSpan={3}>
                            <section
                              aria-label={`${missionName(run)} interface on ${run.robot.name}`}
                            >
                              <strong>
                                {missionName(run)} · {run.robot.name}
                              </strong>
                              <p>
                                Interface access is unavailable for this run.
                              </p>
                              <a
                                href={
                                  resourcePath("runs", run.id, orgId) +
                                  "&tab=App+UI"
                                }
                              >
                                Interface details →
                              </a>
                            </section>
                          </td>
                        </tr>
                      )}
                    </Fragment>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>
      )}
    </main>
  );
}
