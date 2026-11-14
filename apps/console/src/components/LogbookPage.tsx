import { useAuth0 } from "@auth0/auth0-react";
import { useCallback, useEffect, useState } from "react";
import { apiFetch, apiFetchAll } from "@/lib/api";

type Organization = { id: string; name: string };
type Mission = { id: string; name: string };
type Robot = { id: string; name: string };
type Member = { user_id: string; role: "owner" | "developer" | "operator" };
type Revision = { id: string; version: number };
type Execution = {
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
  local_sequence: number;
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

const DIRECT = "__direct__";
const UNKNOWN_MISSION = "__unknown_mission__";
const UNKNOWN_USER = "__unknown_user__";
const UNLINKED = "unlinked:";
const message = (error: unknown) =>
  error instanceof Error ? error.message : "Request failed";
const title = (entry: Entry) =>
  typeof entry.payload.message === "string"
    ? entry.payload.message
    : entry.type === "agent.motion.event" &&
        typeof entry.payload.event_type === "string"
      ? entry.payload.event_type
      : entry.type;
type EventStyle = {
  tone: "note" | "active" | "success" | "danger" | "warning" | "neutral";
  icon: "note" | "activity" | "check" | "error" | "warning" | "neutral";
  label: string;
};
function eventStyle(entry: Entry): EventStyle {
  if (entry.type === "mission.logbook.entry")
    return { tone: "note", icon: "note", label: entry.type };
  const subtype =
    entry.type === "agent.motion.event" &&
    typeof entry.payload.event_type === "string"
      ? entry.payload.event_type
      : entry.type === "device.operation.state_changed" &&
          typeof entry.payload.state === "string"
        ? `${entry.type} · ${entry.payload.state}`
        : entry.type;
  const outcome =
    entry.type === "agent.motion.event"
      ? entry.payload.event_type
      : entry.type === "device.operation.state_changed"
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
    return { tone: "danger", icon: "error", label: subtype };
  if (outcome === "motion.history_incomplete")
    return { tone: "warning", icon: "warning", label: subtype };
  if (
    ["motion.completed", "motion.stop_verified", "succeeded"].includes(
      String(outcome),
    )
  )
    return { tone: "success", icon: "check", label: subtype };
  if (["motion.accepted", "accepted", "running"].includes(String(outcome)))
    return { tone: "active", icon: "activity", label: subtype };
  return { tone: "neutral", icon: "neutral", label: subtype };
}
function EventIcon({ icon }: { icon: EventStyle["icon"] }) {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.7"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      {icon === "note" && (
        <path d="M5 3h14v18H5zM8 3v18M11 8h5M11 12h5M11 16h4" />
      )}
      {icon === "activity" && (
        <>
          <circle cx="12" cy="12" r="9" />
          <path d="M4 12h4l2-4 3 8 2-4h5" />
        </>
      )}
      {icon === "check" && (
        <>
          <circle cx="12" cy="12" r="9" />
          <path d="m8 12 3 3 5-6" />
        </>
      )}
      {icon === "error" && (
        <>
          <circle cx="12" cy="12" r="9" />
          <path d="m9 9 6 6m0-6-6 6" />
        </>
      )}
      {icon === "warning" && <path d="M12 3 2 21h20L12 3zM12 9v5m0 3v.01" />}
      {icon === "neutral" && (
        <>
          <circle cx="12" cy="12" r="9" />
          <path d="M8 12h8" />
        </>
      )}
    </svg>
  );
}
const missionKey = (run: Run) =>
  run.mission?.id ?? (run.mission_revision_id ? UNKNOWN_MISSION : DIRECT);
const missionName = (run: Run) =>
  run.mission?.name ??
  (run.mission_revision_id ? "Unknown mission" : "Direct run");
const humanName = (id: string | null, me: string) =>
  !id ? "Unknown user" : id === me ? "You" : `User ${id}`;
const timestamp = (date: string) => new Date(date).toLocaleString();

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

export function LogbookPage({
  preferredOrganizationId,
  onOrganizationChange,
}: {
  preferredOrganizationId: string;
  onOrganizationChange: (id: string) => void;
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
  const [runs, setRuns] = useState<Run[] | null>(null);
  const [missionFilter, setMissionFilter] = useState("");
  const [robotFilter, setRobotFilter] = useState("");
  const [humanFilter, setHumanFilter] = useState("");
  const [selection, setSelection] = useState("");
  const [entries, setEntries] = useState<Entry[] | null>(null);
  const [output, setOutput] = useState<Output | null>(null);
  const [error, setError] = useState("");
  const [entriesError, setEntriesError] = useState("");
  const [outputError, setOutputError] = useState("");
  const [refresh, setRefresh] = useState(0);

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
      .catch((e) => {
        if (!cancelled) setError(message(e));
      });
    // Identity labels are optional; the execution's user ID remains the source of truth.
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

  // biome-ignore lint/correctness/useExhaustiveDependencies: refresh requests a new snapshot
  useEffect(() => {
    if (!orgId) return;
    let cancelled = false;
    setError("");
    setRuns(null);
    setMembers([]);
    setSelection("");
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
        allRuns.sort((a, b) => b.created_at.localeCompare(a.created_at));
        setMissions(missionList);
        setRobots(robotList);
        setMembers(memberList);
        setRuns(allRuns);
      })
      .catch((e) => {
        if (!cancelled) setError(message(e));
      });
    return () => {
      cancelled = true;
    };
  }, [orgId, getToken, refresh]);

  const rolesByUser = new Map(
    members.map((member) => [member.user_id, member.role]),
  );
  const visibleRuns = (runs ?? []).filter(
    (run) =>
      (!missionFilter || missionKey(run) === missionFilter) &&
      (!robotFilter || run.robot.id === robotFilter) &&
      (!humanFilter || (run.actor_user_id ?? UNKNOWN_USER) === humanFilter),
  );
  const selectedRun = runs?.find((run) => run.id === selection);
  const unlinkedRobot = selection.startsWith(UNLINKED)
    ? robots.find((robot) => robot.id === selection.slice(UNLINKED.length))
    : undefined;
  const selectedRobot = selectedRun?.robot ?? unlinkedRobot;
  const selectionVisible = selectedRun
    ? visibleRuns.some((run) => run.id === selectedRun.id)
    : !!unlinkedRobot &&
      !missionFilter &&
      !humanFilter &&
      (!robotFilter || robotFilter === unlinkedRobot.id);

  useEffect(() => {
    if (selection && !selectionVisible && runs) setSelection("");
  }, [selection, selectionVisible, runs]);

  useEffect(() => {
    setEntries(null);
    setEntriesError("");
    if (!orgId || !selectedRobot || !selectionVisible || !runs) return;
    let cancelled = false;
    const params = new URLSearchParams({ robot_id: selectedRobot.id });
    if (selectedRun) params.set("execution_id", selectedRun.id);
    apiFetchAll<Entry>(getToken, `/v1/organizations/${orgId}/events?${params}`)
      .then((items) => {
        if (cancelled) return;
        const knownRuns = new Set(
          runs
            .filter((run) => run.robot.id === selectedRobot.id)
            .map((run) => run.id),
        );
        setEntries(
          (selectedRun
            ? items
            : items.filter(
                (item) =>
                  !item.execution_id || !knownRuns.has(item.execution_id),
              )
          )
            .slice()
            .sort(
              (a, b) =>
                a.occurred_at.localeCompare(b.occurred_at) ||
                a.local_sequence - b.local_sequence,
            ),
        );
      })
      .catch((e) => {
        if (!cancelled) setEntriesError(message(e));
      });
    return () => {
      cancelled = true;
    };
  }, [orgId, selectedRobot, selectedRun, selectionVisible, runs, getToken]);

  useEffect(() => {
    setOutput(null);
    setOutputError("");
    if (!selectedRun || !selectionVisible) return;
    let cancelled = false;
    Promise.all([
      readOutput(getToken, selectedRun.id, "stdout"),
      readOutput(getToken, selectedRun.id, "stderr"),
    ])
      .then(([stdout, stderr]) => {
        if (!cancelled)
          setOutput({
            stdout: stdout.text,
            stderr: stderr.text,
            truncated: stdout.truncated || stderr.truncated,
          });
      })
      .catch((e) => {
        if (!cancelled) setOutputError(message(e));
      });
    return () => {
      cancelled = true;
    };
  }, [selectedRun, selectionVisible, getToken]);

  const humans = [
    ...new Set((runs ?? []).map((run) => run.actor_user_id ?? UNKNOWN_USER)),
  ].sort();
  const selectOrganization = (id: string) => {
    setSelection("");
    setMissionFilter("");
    setRobotFilter("");
    setHumanFilter("");
    setOrgId(id);
    onOrganizationChange(id);
  };

  return (
    <div className="logbook-browser">
      <aside className="logbook-sidebar" aria-label="Logbook executions">
        <div className="logbook-sidebar-header">
          <div className="logbook-heading">
            <h1>Logbook</h1>
            <button
              className="button-secondary"
              type="button"
              onClick={() => setRefresh((value) => value + 1)}
            >
              Refresh
            </button>
          </div>
          <label>
            Workspace
            <select
              value={orgId}
              onChange={(event) => selectOrganization(event.target.value)}
            >
              {orgs?.map((org) => (
                <option key={org.id} value={org.id}>
                  {org.name}
                </option>
              ))}
            </select>
          </label>
          <div className="logbook-filters">
            <div className="logbook-filter">
              <label>
                Mission
                <select
                  value={missionFilter}
                  onChange={(event) => setMissionFilter(event.target.value)}
                >
                  <option value="">All missions</option>
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
                </select>
              </label>
              <button
                className="logbook-filter-clear"
                type="button"
                aria-label="Clear mission filter"
                disabled={!missionFilter}
                onClick={() => setMissionFilter("")}
              >
                ×
              </button>
            </div>
            <div className="logbook-filter">
              <label>
                Robot
                <select
                  value={robotFilter}
                  onChange={(event) => setRobotFilter(event.target.value)}
                >
                  <option value="">All robots</option>
                  {robots.map((robot) => (
                    <option key={robot.id} value={robot.id}>
                      {robot.name}
                    </option>
                  ))}
                </select>
              </label>
              <button
                className="logbook-filter-clear"
                type="button"
                aria-label="Clear robot filter"
                disabled={!robotFilter}
                onClick={() => setRobotFilter("")}
              >
                ×
              </button>
            </div>
            <div className="logbook-filter">
              <label>
                Human
                <select
                  value={humanFilter}
                  onChange={(event) => setHumanFilter(event.target.value)}
                >
                  <option value="">All humans</option>
                  {humans.map((id) => (
                    <option key={id} value={id}>
                      {humanName(id === UNKNOWN_USER ? null : id, me)}
                    </option>
                  ))}
                </select>
              </label>
              <button
                className="logbook-filter-clear"
                type="button"
                aria-label="Clear human filter"
                disabled={!humanFilter}
                onClick={() => setHumanFilter("")}
              >
                ×
              </button>
            </div>
          </div>
        </div>
        <div className="logbook-list-heading">
          <h2>Executions</h2>
          <span>{runs ? visibleRuns.length : ""}</span>
        </div>
        {error ? (
          <p role="alert" className="notice-error logbook-list-message">
            {error}
          </p>
        ) : orgs?.length === 0 ? (
          <p className="logbook-list-message">No workspace available.</p>
        ) : runs === null ? (
          <p className="logbook-list-message">Loading executions…</p>
        ) : (
          <div className="logbook-list">
            {visibleRuns.map((run) => (
              <article
                key={run.id}
                className={`logbook-run-card${selection === run.id ? " is-selected" : ""}`}
              >
                <div className="logbook-run-title">
                  <button
                    className="logbook-filter-link"
                    type="button"
                    aria-label={`Filter by mission ${missionName(run)}`}
                    onClick={() => setMissionFilter(missionKey(run))}
                  >
                    {missionName(run)}
                  </button>{" "}
                  at{" "}
                  <button
                    className="logbook-run-select"
                    type="button"
                    aria-label={`Open execution ${run.id}`}
                    aria-pressed={selection === run.id}
                    onClick={() => setSelection(run.id)}
                  >
                    {timestamp(run.created_at)}
                  </button>
                </div>
                <div className="logbook-run-meta">
                  <div>
                    by:{" "}
                    <button
                      className="logbook-filter-link"
                      type="button"
                      aria-label={`Filter by human ${humanName(run.actor_user_id, me)}`}
                      onClick={() =>
                        setHumanFilter(run.actor_user_id ?? UNKNOWN_USER)
                      }
                    >
                      {humanName(run.actor_user_id, me)}
                    </button>
                  </div>
                  <div>
                    as:{" "}
                    {run.actor_user_id
                      ? (rolesByUser.get(run.actor_user_id) ?? "unknown")
                      : "unknown"}{" "}
                    (current)
                  </div>
                  <div>
                    on:{" "}
                    <button
                      className="logbook-filter-link"
                      type="button"
                      aria-label={`Filter by robot ${run.robot.name}`}
                      onClick={() => setRobotFilter(run.robot.id)}
                    >
                      {run.robot.name}
                    </button>
                  </div>
                </div>
                <div className="logbook-run-state">
                  <span className="logbook-status" data-state={run.state}>
                    {run.state}
                  </span>
                  {run.version !== undefined ? ` · v${run.version}` : ""}
                </div>
              </article>
            ))}
            {visibleRuns.length === 0 && (
              <p className="logbook-list-message">
                No executions match these filters.
              </p>
            )}
            {!missionFilter &&
              !humanFilter &&
              robots
                .filter((robot) => !robotFilter || robot.id === robotFilter)
                .map((robot) => (
                  <button
                    key={robot.id}
                    className="logbook-unlinked"
                    type="button"
                    aria-pressed={selection === UNLINKED + robot.id}
                    onClick={() => setSelection(UNLINKED + robot.id)}
                  >
                    Unlinked robot events <span>{robot.name}</span>
                  </button>
                ))}
          </div>
        )}
      </aside>
      <main className="logbook-results">
        {!selection || !selectionVisible ? (
          <div className="logbook-placeholder">
            <h2>Execution timeline</h2>
            <p>Select an execution to see its Logbook events.</p>
          </div>
        ) : (
          <div className="logbook-results-inner">
            <header className="logbook-results-header">
              <h1>
                {selectedRun
                  ? missionName(selectedRun)
                  : "Unlinked robot events"}
              </h1>
              <p>
                {selectedRun ? (
                  <>
                    {timestamp(selectedRun.created_at)} ·{" "}
                    <span
                      className="logbook-status"
                      data-state={selectedRun.state}
                    >
                      {selectedRun.state}
                    </span>{" "}
                    · {selectedRun.robot.name}
                  </>
                ) : (
                  selectedRobot?.name
                )}
              </p>
              {selectedRun && (
                <p className="logbook-execution-id">
                  Execution {selectedRun.id}
                </p>
              )}
            </header>
            {unlinkedRobot && (
              <p className="logbook-hint">
                These events are not linked to a listed execution. Agent motion
                events may appear here.
              </p>
            )}
            <section aria-label="Logbook timeline" className="logbook-timeline">
              <h2>Timeline</h2>
              {entriesError ? (
                <p role="alert" className="notice-error">
                  {entriesError}
                </p>
              ) : entries === null ? (
                <p>Loading events…</p>
              ) : entries.length === 0 ? (
                <p>No Logbook events for this selection.</p>
              ) : (
                entries.map((entry) => {
                  const style = eventStyle(entry);
                  return (
                    <details
                      className="logbook-event"
                      data-tone={style.tone}
                      key={entry.event_id}
                    >
                      <summary>
                        <span className="logbook-event-icon">
                          <EventIcon icon={style.icon} />
                        </span>
                        <span className="logbook-event-content">
                          <span className="logbook-event-title">
                            {title(entry)}
                          </span>
                          {title(entry) !== style.label && (
                            <span className="logbook-event-type">
                              {style.label}
                            </span>
                          )}
                        </span>
                        <time dateTime={entry.occurred_at}>
                          {timestamp(entry.occurred_at)}
                        </time>
                        <span
                          className="logbook-event-expand"
                          aria-hidden="true"
                        >
                          +
                        </span>
                      </summary>
                      <div className="logbook-event-details">
                        <dl>
                          <dt>Type</dt>
                          <dd>{entry.type}</dd>
                          <dt>Robot</dt>
                          <dd>{entry.robot_id}</dd>
                          <dt>Execution</dt>
                          <dd>{entry.execution_id ?? "—"}</dd>
                          <dt>Operation</dt>
                          <dd>{entry.operation_id ?? "—"}</dd>
                          <dt>Initiating user</dt>
                          <dd>{entry.actor_user_id ?? "—"}</dd>
                          <dt>Device sequence</dt>
                          <dd>{entry.local_sequence}</dd>
                          <dt>Received</dt>
                          <dd>{timestamp(entry.received_at)}</dd>
                        </dl>
                        <h3>Fields</h3>
                        <pre>{JSON.stringify(entry.payload, null, 2)}</pre>
                      </div>
                    </details>
                  );
                })
              )}
            </section>
            {selectedRun && (
              <details className="logbook-output">
                <summary>Execution output</summary>
                {outputError ? (
                  <p role="alert" className="notice-error">
                    {outputError}
                  </p>
                ) : output === null ? (
                  <p>Loading output…</p>
                ) : (
                  <>
                    <h3>Standard output</h3>
                    <pre>{output.stdout || "No output."}</pre>
                    <h3>Standard error</h3>
                    <pre>{output.stderr || "No errors."}</pre>
                    {output.truncated && <p>Output was truncated.</p>}
                  </>
                )}
              </details>
            )}
          </div>
        )}
      </main>
    </div>
  );
}
