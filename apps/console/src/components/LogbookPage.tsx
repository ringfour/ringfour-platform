import { useAuth0 } from "@auth0/auth0-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import {
  type Artifact,
  ArtifactCard,
  ArtifactGallery,
} from "@/components/ArtifactGallery";
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
const message = (error: unknown) =>
  error instanceof Error ? error.message : "Request failed";
const timestamp = (date: string) => new Date(date).toLocaleString();
const missionKey = (run: Run) =>
  run.mission?.id ?? (run.mission_revision_id ? UNKNOWN_MISSION : DIRECT);
const missionName = (run: Run) =>
  run.mission?.name ??
  (run.mission_revision_id ? "Unknown mission" : "Direct run");
const humanName = (id: string | null, me: string) =>
  !id ? "Unknown user" : id === me ? "You" : `User ${id}`;
const entryTitle = (entry: Entry) =>
  typeof entry.payload.message === "string"
    ? entry.payload.message
    : typeof entry.payload.event_type === "string"
      ? entry.payload.event_type
      : entry.type;
const evidenceIds = (entry: Entry) =>
  Array.isArray(entry.payload.evidence)
    ? entry.payload.evidence.filter(
        (value): value is string => typeof value === "string",
      )
    : [];

function eventTone(entry: Entry) {
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
  onOrganizationChange,
  pollIntervalMs = 3000,
}: {
  preferredOrganizationId: string;
  onOrganizationChange: (id: string) => void;
  pollIntervalMs?: number;
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
  const [stateFilter, setStateFilter] = useState("");
  const [selection, setSelection] = useState("");
  const [entries, setEntries] = useState<Entry[] | null>(null);
  const [output, setOutput] = useState<Output | null>(null);
  const [artifacts, setArtifacts] = useState<Artifact[] | null>(null);
  const [artifactError, setArtifactError] = useState("");
  const [error, setError] = useState("");
  const [entriesError, setEntriesError] = useState("");
  const [outputError, setOutputError] = useState("");
  const [refresh, setRefresh] = useState(0);
  const [detailRefresh, setDetailRefresh] = useState(0);

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

  // biome-ignore lint/correctness/useExhaustiveDependencies: refresh requests a new snapshot
  useEffect(() => {
    if (!orgId) return;
    let cancelled = false;
    setError("");
    setRuns(null);
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
          (!missionFilter || missionKey(run) === missionFilter) &&
          (!robotFilter || run.robot.id === robotFilter) &&
          (!humanFilter ||
            (run.actor_user_id ?? UNKNOWN_USER) === humanFilter) &&
          (!stateFilter || run.state === stateFilter),
      ),
    [runs, missionFilter, robotFilter, humanFilter, stateFilter],
  );
  const selectedRun = runs?.find((run) => run.id === selection);
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

  // biome-ignore lint/correctness/useExhaustiveDependencies: detailRefresh requests a new snapshot
  useEffect(() => {
    setEntries(null);
    setOutput(null);
    setEntriesError("");
    setOutputError("");
    if (!orgId || !selectedRun) return;
    let cancelled = false;
    const params = new URLSearchParams({
      robot_id: selectedRun.robot.id,
      operation_id: selectedRun.id,
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
                left.local_sequence - right.local_sequence,
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
    const timer = window.setInterval(() => void load(), pollIntervalMs);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [getToken, pollIntervalMs, selectedRun]);

  const clearFilters = () => {
    setMissionFilter("");
    setRobotFilter("");
    setHumanFilter("");
    setStateFilter("");
  };
  const selectOrganization = (id: string) => {
    setOrgId(id);
    setSelection("");
    clearFilters();
    onOrganizationChange(id);
  };
  const openRun = (id: string) => {
    setSelection(id);
    window.scrollTo({ top: 0, behavior: "auto" });
  };

  if (selectedRun) {
    return (
      <main className="logbook2-page logbook2-detail">
        <header className="logbook2-detail-toolbar">
          <button
            className="logbook2-back"
            type="button"
            onClick={() => setSelection("")}
          >
            <span aria-hidden="true">←</span> Back to logbook
          </button>
          <button
            className="button-secondary"
            type="button"
            onClick={() => setDetailRefresh((value) => value + 1)}
          >
            Refresh
          </button>
        </header>
        <section className="logbook2-record-header">
          <div className="logbook2-record-heading">
            <div>
              <span className="logbook2-kicker">Execution record</span>
              <h1>{missionName(selectedRun)}</h1>
            </div>
            <span className="logbook2-state" data-state={selectedRun.state}>
              {selectedRun.state}
            </span>
          </div>
          <dl className="logbook2-record-meta">
            <div>
              <dt>Started</dt>
              <dd>{timestamp(selectedRun.created_at)}</dd>
            </div>
            <div>
              <dt>Robot</dt>
              <dd>{selectedRun.robot.name}</dd>
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
            <div>
              <dt>Execution ID</dt>
              <dd className="logbook2-mono">{selectedRun.id}</dd>
            </div>
          </dl>
        </section>

        <section className="logbook2-section" aria-label="Logbook timeline">
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
                        {evidenceIds(entry).length > 0 &&
                          ` · ${evidenceIds(entry).length} evidence file${evidenceIds(entry).length === 1 ? "" : "s"}`}
                      </small>
                    </span>
                    <time dateTime={entry.occurred_at}>
                      {timestamp(entry.occurred_at)}
                    </time>
                    <span className="logbook2-event-open" aria-hidden="true">
                      +
                    </span>
                  </summary>
                  <div className="logbook2-event-detail">
                    <dl>
                      <dt>Sequence</dt>
                      <dd>{entry.local_sequence}</dd>
                      <dt>Operation</dt>
                      <dd>{entry.operation_id ?? "—"}</dd>
                      <dt>Received</dt>
                      <dd>{timestamp(entry.received_at)}</dd>
                    </dl>
                    <pre>{JSON.stringify(entry.payload, null, 2)}</pre>
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
        <ArtifactGallery
          artifacts={artifacts}
          executionId={selectedRun.id}
          getToken={getToken}
          error={artifactError}
          pollIntervalMs={pollIntervalMs}
          sectionIndex="02"
        />
        <section className="logbook2-section" aria-label="Execution output">
          <header className="logbook2-section-heading">
            <div>
              <span className="logbook2-section-index">03</span>
              <h2>Execution output</h2>
            </div>
            {output?.truncated && <span>Truncated</span>}
          </header>
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
          <h1>Logbook</h1>
          <p>Mission executions and their recorded operational history.</p>
        </div>
        <button
          className="button-secondary"
          type="button"
          onClick={() => setRefresh((value) => value + 1)}
        >
          Refresh
        </button>
      </header>

      <section className="logbook2-filters" aria-label="Logbook filters">
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
            label="Workspace"
            value={orgId}
            onChange={selectOrganization}
          >
            {orgs?.map((org) => (
              <option key={org.id} value={org.id}>
                {org.name}
              </option>
            ))}
          </FilterField>
          <FilterField
            label="Mission"
            value={missionFilter}
            onChange={setMissionFilter}
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
          </FilterField>
          <FilterField
            label="Robot"
            value={robotFilter}
            onChange={setRobotFilter}
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
            onChange={setHumanFilter}
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
            onChange={setStateFilter}
          >
            <option value="">All statuses</option>
            {states.map((state) => (
              <option key={state} value={state}>
                {state}
              </option>
            ))}
          </FilterField>
        </div>
      </section>

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
                  <th>Mission</th>
                  <th>Robot</th>
                  <th>Started</th>
                  <th>Initiated by</th>
                  <th>Role</th>
                  <th>Status</th>
                  <th aria-label="Open record" />
                </tr>
              </thead>
              <tbody>
                {visibleRuns.map((run) => (
                  <tr
                    key={run.id}
                    tabIndex={0}
                    aria-label={`Open ${missionName(run)} execution from ${timestamp(run.created_at)}`}
                    onClick={() => openRun(run.id)}
                    onKeyDown={(event) => {
                      if (event.key === "Enter" || event.key === " ") {
                        event.preventDefault();
                        openRun(run.id);
                      }
                    }}
                  >
                    <td>
                      <strong>{missionName(run)}</strong>
                      <small>
                        {run.version === undefined
                          ? "Unversioned"
                          : `Revision ${run.version}`}
                      </small>
                    </td>
                    <td>{run.robot.name}</td>
                    <td className="logbook2-nowrap">
                      {timestamp(run.created_at)}
                    </td>
                    <td>{humanName(run.actor_user_id, me)}</td>
                    <td>
                      {run.actor_user_id
                        ? (rolesByUser.get(run.actor_user_id) ?? "unknown")
                        : "unknown"}
                    </td>
                    <td>
                      <span className="logbook2-state" data-state={run.state}>
                        {run.state}
                      </span>
                    </td>
                    <td className="logbook2-open" aria-hidden="true">
                      →
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </main>
  );
}
