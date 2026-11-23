import { useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { apiFetchAll } from "@/lib/api";
import { resourcePath, useLocation } from "@/lib/location";
import { StudioTime } from "./StudioTime";
import "./studio.css";

type Robot = { id: string; name: string };
type Clip = {
  id: string;
  robot: Robot;
  app: string;
  version?: number;
  state: string;
  created_at: string;
  updated_at?: string;
  person?: string;
};
type Marker = {
  event_id: string;
  robot_id: string;
  execution_id: string | null;
  type: string;
  occurred_at: string;
  received_at: string;
  payload: { message?: string; evidence?: unknown[] };
};
type TimelineTooltip = {
  id: string;
  heading: string;
  detail: string;
  left: number;
  top: number;
};
const eventLabel = (event: Marker) => {
  if (Array.isArray(event.payload?.evidence) && event.payload.evidence.length)
    return "Evidence recorded";
  if (event.type === "human.interaction.requested") return "Decision requested";
  if (event.type === "human.interaction.answered") return "Decision answered";
  if (event.type === "mission.failed") return "Run failed";
  return event.type.replaceAll(".", " ");
};
const hour = 3600000;
const clock = (time: number) =>
  new Date(time).toLocaleTimeString(undefined, {
    hour: "2-digit",
    minute: "2-digit",
  });
export function RunTimeline({
  robots,
  runs,
  workspace,
  token,
  preview = false,
  robotFilter,
  focusedRun,
}: {
  robots: Robot[];
  runs: Clip[];
  workspace: string;
  token: () => Promise<string>;
  preview?: boolean;
  robotFilter?: string;
  focusedRun?: Clip;
}) {
  const query = useLocation().searchParams;
  const runStart = focusedRun ? Date.parse(focusedRun.created_at) : NaN;
  const runEnd =
    focusedRun && !["running", "preparing", "queued"].includes(focusedRun.state)
      ? Date.parse(focusedRun.updated_at ?? "")
      : Date.now();
  const latestRecorded = Math.max(
    0,
    ...runs
      .map((run) => Date.parse(run.updated_at ?? run.created_at))
      .filter(Number.isFinite),
  );
  const hasActive = runs.some((run) =>
    ["running", "queued", "preparing"].includes(run.state),
  );
  const recordedEnd = hasActive
    ? Date.now()
    : latestRecorded
      ? latestRecorded + 600000
      : Date.now();
  const initialEnd =
    focusedRun && Number.isFinite(runEnd)
      ? runEnd
      : Number(query.get("until")) || recordedEnd;
  const initialHours = Number(query.get("hours"));
  const [end, setEnd] = useState(
    initialEnd > 0 && Number.isFinite(new Date(initialEnd).getTime())
      ? initialEnd
      : recordedEnd,
  );
  const [hours, setHours] = useState(
    focusedRun && Number.isFinite(runStart) && Number.isFinite(runEnd)
      ? Math.max(1 / 60, (runEnd - runStart) / hour)
      : initialHours > 0 &&
          Number.isFinite(initialHours * hour) &&
          Number.isFinite(
            new Date(
              (initialEnd || Date.now()) - initialHours * hour,
            ).getTime(),
          )
        ? initialHours
        : 6,
  );
  const [follow, setFollow] = useState(
    !query.get("until") && hasActive && !focusedRun,
  );
  const [selection, select] = useState("");
  const [tooltip, setTooltip] = useState<TimelineTooltip | null>(null);
  const [events, setEvents] = useState<Marker[] | null>(null);
  const [error, setError] = useState(false);
  const span = hours * hour;
  const start = end - span;
  useEffect(() => {
    if (!follow) return;
    const timer = window.setInterval(() => setEnd(Date.now()), 30000);
    return () => window.clearInterval(timer);
  }, [follow]);
  useEffect(() => {
    let current = true;
    setError(false);
    const range = new URLSearchParams({
      from: new Date(start).toISOString(),
      to: new Date(end).toISOString(),
    });
    if (robotFilter) range.set("robot_id", robotFilter);
    if (focusedRun) range.set("execution_id", focusedRun.id);
    apiFetchAll<Marker>(
      token,
      "/v1/organizations/" + encodeURIComponent(workspace) + "/events?" + range,
    )
      .then((items) => {
        if (current)
          setEvents(
            items.filter(
              (e) =>
                e.event_id &&
                (/interaction|fail/.test(e.type) ||
                  (e.type === "mission.logbook.entry" &&
                    Array.isArray(e.payload?.evidence) &&
                    e.payload.evidence.length > 0)),
            ),
          );
      })
      .catch(() => {
        if (current) setError(true);
      });
    return () => {
      current = false;
    };
  }, [workspace, token, start, end, robotFilter, focusedRun?.id]);
  const drag = useRef<{ x: number; end: number; width: number } | null>(null);
  const timelineElement = useRef<HTMLElement | null>(null);
  useEffect(() => {
    const element = timelineElement.current;
    if (!element || preview) return;
    const wheel = (event: WheelEvent) => {
      const track = (event.target as Element).closest(".timeline-lanes");
      if (!track || !element.contains(track)) return;
      event.preventDefault();
      setFollow(false);
      if (event.ctrlKey || event.metaKey) {
        setHours((value) =>
          Math.min(
            168,
            Math.max(0.25, value * (event.deltaY > 0 ? 1.15 : 1 / 1.15)),
          ),
        );
      } else {
        setEnd(
          (value) =>
            value +
            ((event.deltaX || event.deltaY) * span) /
              Math.max(1, track.clientWidth),
        );
      }
    };
    element.addEventListener("wheel", wheel, { passive: false });
    return () => element.removeEventListener("wheel", wheel);
  }, [preview, span]);
  const [trackPage, setTrackPage] = useState(0);
  const pages = Math.max(1, Math.ceil(robots.length / 40));
  const page = Math.min(trackPage, pages - 1);
  const tracks = preview
    ? robots.slice(0, 4)
    : robots.slice(page * 40, (page + 1) * 40);
  useEffect(() => {
    if (preview || follow) return;
    const location = new URL(window.location.href);
    location.searchParams.set("until", String(end));
    location.searchParams.set("hours", String(hours));
    window.history.replaceState(window.history.state, "", location);
  }, [preview, follow, end, hours]);
  const chosenRun = runs.find((run) => run.id === selection);
  const chosenEvent = events?.find((event) => event.event_id === selection);
  function inspect(id: string) {
    setFollow(false);
    select(id);
    setTooltip(null);
  }
  function showTooltip(
    element: HTMLElement,
    id: string,
    heading: string,
    detail: string,
  ) {
    const rect = element.getBoundingClientRect();
    const width = Math.min(260, window.innerWidth - 24);
    setTooltip({
      id,
      heading,
      detail,
      left: Math.max(12, Math.min(rect.left, window.innerWidth - width - 12)),
      top:
        rect.bottom + 88 < window.innerHeight
          ? rect.bottom + 8
          : Math.max(8, rect.top - 88),
    });
  }
  const [customError, setCustomError] = useState("");
  function move(to: number) {
    setFollow(false);
    setEnd(to);
    select("");
  }
  function zoom(factor: number) {
    const next = Math.min(168, Math.max(0.25, hours * factor));
    move(end + ((next - hours) * hour) / 2);
    setHours(next);
  }
  const position = (t: number) =>
    Math.max(0, Math.min(100, ((t - start) * 100) / span));
  const now = Date.now();
  const toolbarHost =
    typeof document !== "undefined"
      ? document.getElementById("console-timeline-toolbar")
      : null;
  const toolbar = !preview ? (
    <div className="timeline-toolbar-content">
      <div className="timeline-tools">
        <label>
          Window{" "}
          <select
            value={hours}
            onChange={(e) => {
              setHours(Number(e.target.value));
              setFollow(false);
            }}
          >
            {[...new Set([1, 6, 24, 72, hours])]
              .sort((a, b) => a - b)
              .map((h) => (
                <option key={h} value={h}>
                  {Number(h.toFixed(2))} hours
                </option>
              ))}
          </select>
        </label>
        <button
          type="button"
          onClick={() => {
            const midnight = new Date();
            midnight.setHours(0, 0, 0, 0);
            const current = Date.now();
            setHours(Math.max(1 / 60, (current - midnight.getTime()) / hour));
            move(current);
          }}
        >
          Today
        </button>
        <button
          type="button"
          aria-label="Pan earlier"
          onClick={() => move(end - span / 2)}
        >
          ←
        </button>
        <button
          type="button"
          aria-label="Pan later"
          onClick={() => move(end + span / 2)}
        >
          →
        </button>
        <button type="button" aria-label="Zoom in" onClick={() => zoom(0.5)}>
          +
        </button>
        <button type="button" aria-label="Zoom out" onClick={() => zoom(2)}>
          −
        </button>
        <button
          type="button"
          onClick={() => {
            setEnd(Date.now());
            setFollow(true);
          }}
        >
          Jump to now
        </button>
        <label>
          <input
            type="checkbox"
            checked={follow}
            onChange={(e) => {
              setFollow(e.target.checked);
              if (e.target.checked) setEnd(Date.now());
            }}
          />{" "}
          Live follow
        </label>
      </div>
      <details>
        <summary>Custom time range</summary>
        <form
          className="timeline-tools"
          onSubmit={(e) => {
            e.preventDefault();
            const fields = new FormData(e.currentTarget);
            const from = Date.parse(String(fields.get("from")));
            const to = Date.parse(String(fields.get("to")));
            if (!(to > from)) {
              setCustomError("Choose an end after the start.");
              return;
            }
            setCustomError("");
            setHours((to - from) / hour);
            move(to);
          }}
        >
          <label>
            From <input required name="from" type="datetime-local" />
          </label>
          <label>
            To <input required name="to" type="datetime-local" />
          </label>
          <button type="submit">Apply range</button>
          {customError && <span role="alert">{customError}</span>}
        </form>
      </details>
    </div>
  ) : null;
  return (
    <section
      ref={timelineElement}
      className="studio-timeline"
      aria-label="Robot timeline"
    >
      <header>
        <h2>
          {focusedRun
            ? "Run duration"
            : preview
              ? "Recent timeline"
              : "Robot timeline"}
        </h2>
        {preview && (
          <a
            href={
              resourcePath("timeline", "", workspace) +
              "&hours=" +
              hours +
              "&until=" +
              end +
              (robotFilter ? "&robot=" + encodeURIComponent(robotFilter) : "")
            }
          >
            Open full timeline →
          </a>
        )}
      </header>
      <p>
        {new Date(start).toLocaleDateString()} · {clock(start)} –{" "}
        {new Date(end).toLocaleDateString()} · {clock(end)} (
        {Intl.DateTimeFormat().resolvedOptions().timeZone})
      </p>
      {toolbarHost ? createPortal(toolbar, toolbarHost) : toolbar}
      {!preview && (
        <p className="timeline-hint">
          Drag a track to pan · Scroll to pan · Ctrl or ⌘ + scroll to zoom
        </p>
      )}
      <div className="timeline-track timeline-axis">
        <span>Robot</span>
        <div>
          {[0, 0.5, 1].map((f) => (
            <small key={f}>{clock(start + span * f)}</small>
          ))}
        </div>
      </div>
      {tracks.map((robot) => {
        const clips = runs
          .filter((run) => run.robot.id === robot.id)
          .map((run) => {
            const left = Date.parse(run.created_at);
            const live = ["running", "preparing", "queued"].includes(run.state);
            const knownEnd =
              ["succeeded", "cancelled", "failed"].includes(run.state) &&
              !!run.updated_at;
            const right = live
              ? now
              : knownEnd
                ? Date.parse(run.updated_at!)
                : left;
            return { run, left, right, uncertain: !live && !knownEnd };
          })
          .filter((c) => c.left <= end && c.right >= start)
          .sort((a, b) => a.left - b.left);
        const laneHeight = 48;
        const laneOffset = 24;
        const lanes: { app: string; end: number }[] = [];
        const placedClips = clips.map((clip) => {
          let lane = lanes.findIndex(
            (item) =>
              item.app === clip.run.app &&
              item.end <= Math.max(clip.left, start),
          );
          if (lane < 0) {
            lane = lanes.length;
            lanes.push({ app: clip.run.app, end: 0 });
          }
          lanes[lane].end = Math.max(clip.right, clip.left + span * 0.025);
          return { ...clip, lane };
        });
        const trackHeight =
          laneOffset + Math.max(1, lanes.length) * laneHeight + 8;
        const markers = (events ?? []).filter(
          (e) =>
            e.robot_id === robot.id &&
            Date.parse(e.occurred_at) >= start &&
            Date.parse(e.occurred_at) <= end,
        );
        const groups = new Map<string, { lane: number; items: Marker[] }>();
        for (const marker of markers) {
          const lane =
            placedClips.find((clip) => clip.run.id === marker.execution_id)
              ?.lane ?? 0;
          const bucket = Math.min(
            11,
            Math.floor((position(Date.parse(marker.occurred_at)) * 12) / 100),
          );
          const key = lane + ":" + bucket;
          const existing = groups.get(key);
          if (existing) existing.items.push(marker);
          else groups.set(key, { lane, items: [marker] });
        }
        return (
          <div className="timeline-track" key={robot.id}>
            <div
              className="timeline-track-label"
              style={{ minHeight: trackHeight }}
            >
              <a
                href={resourcePath("robots", robot.id, workspace)}
                title={robot.name}
              >
                {robot.name}
              </a>
              {lanes.map((lane, index) => (
                <span
                  className="timeline-lane-name"
                  key={index}
                  style={{ top: laneOffset + index * laneHeight + 20 }}
                  title={lane.app}
                >
                  {lane.app}
                </span>
              ))}
            </div>
            <div
              className="timeline-lanes"
              onPointerDown={(event) => {
                if (preview || event.target !== event.currentTarget) return;
                drag.current = {
                  x: event.clientX,
                  end,
                  width: event.currentTarget.clientWidth,
                };
                event.currentTarget.setPointerCapture(event.pointerId);
              }}
              onPointerMove={(event) => {
                if (!drag.current) return;
                setFollow(false);
                setEnd(
                  drag.current.end -
                    ((event.clientX - drag.current.x) * span) /
                      Math.max(1, drag.current.width),
                );
              }}
              onPointerUp={(event) => {
                drag.current = null;
                if (event.currentTarget.hasPointerCapture?.(event.pointerId))
                  event.currentTarget.releasePointerCapture(event.pointerId);
              }}
            >
              <div
                className="timeline-rail"
                style={{ height: trackHeight - laneOffset }}
                aria-hidden="true"
              />
              {now >= start && now < end + 60000 && (
                <span
                  className="timeline-now"
                  style={{ left: position(now) + "%", height: trackHeight }}
                  title="Now"
                />
              )}
              {placedClips.map(({ run, left, right, uncertain, lane }) => {
                const label =
                  run.app +
                  (run.version ? " v" + run.version : "") +
                  " · " +
                  run.state +
                  (uncertain ? " · End unknown" : "");
                return (
                  <button
                    type="button"
                    className="timeline-clip"
                    key={run.id}
                    data-state={run.state}
                    aria-pressed={selection === run.id}
                    aria-label={label}
                    aria-describedby={
                      tooltip?.id === run.id
                        ? "timeline-floating-tooltip"
                        : undefined
                    }
                    onMouseEnter={(event) =>
                      showTooltip(
                        event.currentTarget,
                        run.id,
                        `${run.app}${run.version ? " v" + run.version : ""}`,
                        `${run.state} · ${clock(left)} – ${["running", "preparing", "queued"].includes(run.state) ? "Now" : clock(right)}`,
                      )
                    }
                    onFocus={(event) =>
                      showTooltip(
                        event.currentTarget,
                        run.id,
                        `${run.app}${run.version ? " v" + run.version : ""}`,
                        `${run.state} · ${clock(left)} – ${["running", "preparing", "queued"].includes(run.state) ? "Now" : clock(right)}`,
                      )
                    }
                    onMouseLeave={() => setTooltip(null)}
                    onBlur={() => setTooltip(null)}
                    onClick={() => inspect(run.id)}
                    style={{
                      left: position(left) + "%",
                      width: position(right) - position(left) + "%",
                      top: laneOffset + lane * laneHeight + 2,
                    }}
                  >
                    {label}
                  </button>
                );
              })}
              <div style={{ height: trackHeight }} />
              {groups.size > 0 && (
                <div
                  className="timeline-markers"
                  style={{ height: trackHeight }}
                >
                  {Array.from(groups, ([key, { lane, items }]) => (
                    <button
                      key={key}
                      type="button"
                      data-kind={
                        items.some((item) => /fail/.test(item.type))
                          ? "failure"
                          : items.some(
                                (item) =>
                                  Array.isArray(item.payload?.evidence) &&
                                  item.payload.evidence.length > 0,
                              )
                            ? "evidence"
                            : "interaction"
                      }
                      style={{
                        left:
                          Math.min(
                            98,
                            Math.max(
                              2,
                              position(Date.parse(items[0].occurred_at)),
                            ),
                          ) + "%",
                        top: laneOffset + lane * laneHeight + 35,
                      }}
                      aria-label={
                        items.length === 1
                          ? eventLabel(items[0]) +
                            (items[0].payload?.message
                              ? ": " + items[0].payload.message
                              : "")
                          : items.length +
                            " events near " +
                            clock(Date.parse(items[0].occurred_at))
                      }
                      aria-describedby={
                        tooltip?.id === key
                          ? "timeline-floating-tooltip"
                          : undefined
                      }
                      onMouseEnter={(event) =>
                        showTooltip(
                          event.currentTarget,
                          key,
                          items.length === 1
                            ? eventLabel(items[0])
                            : `${items.length} events`,
                          items.length === 1
                            ? `${items[0].payload?.message ?? "Recorded event"} · ${clock(Date.parse(items[0].occurred_at))}`
                            : `Near ${clock(Date.parse(items[0].occurred_at))}`,
                        )
                      }
                      onFocus={(event) =>
                        showTooltip(
                          event.currentTarget,
                          key,
                          items.length === 1
                            ? eventLabel(items[0])
                            : `${items.length} events`,
                          items.length === 1
                            ? `${items[0].payload?.message ?? "Recorded event"} · ${clock(Date.parse(items[0].occurred_at))}`
                            : `Near ${clock(Date.parse(items[0].occurred_at))}`,
                        )
                      }
                      onMouseLeave={() => setTooltip(null)}
                      onBlur={() => setTooltip(null)}
                      aria-pressed={
                        selection ===
                        (items.length === 1
                          ? items[0].event_id
                          : "group:" + robot.id + ":" + key)
                      }
                      onClick={() =>
                        inspect(
                          items.length === 1
                            ? items[0].event_id
                            : "group:" + robot.id + ":" + key,
                        )
                      }
                    >
                      {items.length === 1 ? "" : items.length}
                    </button>
                  ))}
                </div>
              )}
              {Array.from(
                groups,
                ([key, { items }]) =>
                  selection === "group:" + robot.id + ":" + key && (
                    <div className="timeline-event-card" key={key}>
                      <strong>
                        {items.length} events near{" "}
                        {clock(Date.parse(items[0].occurred_at))}
                      </strong>
                      <div className="timeline-event-list">
                        {items.map((e) => (
                          <button
                            type="button"
                            key={e.event_id}
                            onClick={() => inspect(e.event_id)}
                          >
                            {clock(Date.parse(e.occurred_at))} · {eventLabel(e)}
                          </button>
                        ))}
                      </div>
                    </div>
                  ),
              )}
              {chosenEvent?.robot_id === robot.id && (
                <div
                  className="timeline-event-card"
                  data-kind={
                    /fail/.test(chosenEvent.type)
                      ? "failure"
                      : Array.isArray(chosenEvent.payload?.evidence) &&
                          chosenEvent.payload.evidence.length > 0
                        ? "evidence"
                        : "interaction"
                  }
                  role="region"
                  aria-label="Selected timeline event"
                >
                  <button
                    className="timeline-event-close"
                    type="button"
                    onClick={() => select("")}
                    aria-label="Close event card"
                  >
                    ×
                  </button>
                  <small>{eventLabel(chosenEvent)}</small>
                  <h3>{chosenEvent.payload?.message ?? chosenEvent.type}</h3>
                  <p>
                    Occurred <StudioTime value={chosenEvent.occurred_at} />
                  </p>
                  <p>
                    Received <StudioTime value={chosenEvent.received_at} />
                  </p>
                  {chosenEvent.execution_id && (
                    <a
                      href={resourcePath(
                        "runs",
                        chosenEvent.execution_id,
                        workspace,
                      )}
                    >
                      View run →
                    </a>
                  )}
                </div>
              )}
            </div>
          </div>
        );
      })}
      {!preview && pages > 1 && (
        <div className="timeline-tools">
          <button
            type="button"
            disabled={page === 0}
            onClick={() => setTrackPage(page - 1)}
          >
            Previous robots
          </button>
          <span>
            Page {page + 1} of {pages}
          </span>
          <button
            type="button"
            disabled={page === pages - 1}
            onClick={() => setTrackPage(page + 1)}
          >
            Next robots
          </button>
        </div>
      )}
      {!tracks.length && <p>No robots in this view.</p>}
      {preview && robots.length > tracks.length && (
        <small>
          {robots.length - tracks.length} more robots in the full timeline
        </small>
      )}
      {error && <small role="status">Event markers unavailable.</small>}
      {chosenRun && (
        <div className="timeline-inspector">
          <button type="button" onClick={() => select("")}>
            Close details
          </button>
          {chosenRun && (
            <>
              <h3>
                {chosenRun.app} {chosenRun.version && "v" + chosenRun.version}
              </h3>
              <p>
                {chosenRun.robot.name} · {chosenRun.state}
                {chosenRun.person && " · Started by " + chosenRun.person}
              </p>
              <p>
                Created <StudioTime value={chosenRun.created_at} />
              </p>
              {chosenRun.updated_at && (
                <p>
                  Last updated <StudioTime value={chosenRun.updated_at} />
                </p>
              )}
              <a href={resourcePath("runs", chosenRun.id, workspace)}>
                Open run →
              </a>
            </>
          )}
        </div>
      )}
      {tooltip &&
        createPortal(
          <div
            id="timeline-floating-tooltip"
            className="timeline-floating-tooltip"
            role="tooltip"
            style={{ left: tooltip.left, top: tooltip.top }}
          >
            <strong>{tooltip.heading}</strong>
            <span>{tooltip.detail}</span>
          </div>,
          document.body,
        )}
    </section>
  );
}
