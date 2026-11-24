import { useAuth0 } from "@auth0/auth0-react";
import { useCallback, useEffect, useState } from "react";
import { apiFetch } from "@/lib/api";
import { resourcePath } from "@/lib/location";
import {
  type App,
  type AppLogbookEvent,
  type AppRun,
  type Robot,
  readJson,
  readPage,
  type Token,
  withQuery,
} from "@/lib/apps";
import "./app-workflows.css";

async function readRunOutput(
  token: Token,
  runId: string,
  stream: "stdout" | "stderr",
) {
  const chunks: Uint8Array[] = [];
  let offset = 0;
  let truncated = false;
  for (let pageNumber = 0; pageNumber < 5; pageNumber++) {
    const page = await readJson<{
      next_offset: number;
      data_base64: string;
      sealed: boolean;
      truncated: boolean;
    }>(
      token,
      withQuery(`/v1/app-runs/${runId}/output`, {
        stream,
        offset: String(offset),
      }),
    );
    const bytes = Uint8Array.from(atob(page.data_base64), (character) =>
      character.charCodeAt(0),
    );
    if (bytes.length) chunks.push(bytes);
    truncated ||= page.truncated;
    if (page.sealed || page.next_offset <= offset || bytes.length === 0) break;
    offset = page.next_offset;
  }
  const length = chunks.reduce((total, chunk) => total + chunk.length, 0);
  const joined = new Uint8Array(length);
  let cursor = 0;
  for (const chunk of chunks) {
    joined.set(chunk, cursor);
    cursor += chunk.length;
  }
  return { text: new TextDecoder().decode(joined), truncated };
}

export function AppLogbookPage({
  organizationId,
  runId,
  selectRun,
}: {
  organizationId: string;
  runId?: string;
  selectRun: (id?: string) => void;
}) {
  const { getAccessTokenSilently } = useAuth0();
  const token = useCallback<Token>(async () => {
    const value = await getAccessTokenSilently({
      authorizationParams: { audience: import.meta.env.VITE_AUTH0_AUDIENCE },
    });
    if (!value) throw new Error("Sign in required");
    return value;
  }, [getAccessTokenSilently]);
  const [runs, setRuns] = useState<AppRun[]>([]);
  const [events, setEvents] = useState<AppLogbookEvent[]>([]);
  const [robots, setRobots] = useState<Robot[]>([]);
  const [apps, setApps] = useState<Record<string, App>>({});
  const [output, setOutput] = useState("");
  const [artifacts, setArtifacts] = useState<
    { artifact_id: string; path: string; size: number; content_type: string }[]
  >([]);
  const [error, setError] = useState("");
  const [appFilter, setAppFilter] = useState(
    () => new URLSearchParams(window.location.search).get("app_id") ?? "",
  );
  const [robotFilter, setRobotFilter] = useState("");
  const [stateFilter, setStateFilter] = useState("");
  useEffect(() => {
    let live = true;
    Promise.all([
      readPage<AppRun>(token, `/v1/organizations/${organizationId}/app-runs`),
      readPage<AppLogbookEvent>(
        token,
        `/v1/organizations/${organizationId}/app-logbook`,
      ),
      readPage<Robot>(token, `/v1/organizations/${organizationId}/robots`),
    ])
      .then(async ([r, e, b]) => {
        const pairs = await Promise.all(
          [...new Set(r.map((x) => x.snapshot.app_id))].map(
            async (id) =>
              [id, await readJson<App>(token, `/v1/apps/${id}`)] as const,
          ),
        );
        if (live) {
          setRuns(r);
          setEvents(e);
          setRobots(b);
          setApps(Object.fromEntries(pairs));
        }
      })
      .catch((cause) => {
        if (live)
          setError(
            cause instanceof Error
              ? cause.message
              : "Could not load App history.",
          );
      });
    return () => {
      live = false;
    };
  }, [token, organizationId]);
  useEffect(() => {
    let live = true;
    if (!runId) {
      setOutput("");
      setArtifacts([]);
      return;
    }
    (async () => {
      try {
        const [run, stdout, stderr, files] = await Promise.all([
          readJson<AppRun>(token, `/v1/app-runs/${runId}`),
          readRunOutput(token, runId, "stdout"),
          readRunOutput(token, runId, "stderr"),
          readJson<{
            items: {
              artifact_id: string;
              path: string;
              size: number;
              content_type: string;
            }[];
          }>(token, `/v1/app-runs/${runId}/artifacts`),
        ]);
        if (live) {
          setOutput(
            `stdout\n${stdout.text}${stdout.truncated ? "\n[truncated]" : ""}\nstderr\n${stderr.text}${stderr.truncated ? "\n[truncated]" : ""}\n\nState: ${run.state}${run.failure ? ` · ${run.failure.message}` : ""}`,
          );
          setArtifacts(files.items);
        }
      } catch (cause) {
        if (live)
          setError(
            cause instanceof Error ? cause.message : "Run details unavailable.",
          );
      }
    })();
    return () => {
      live = false;
    };
  }, [token, runId]);
  const selected = runs.find((x) => x.id === runId);
  const title = (run: AppRun) =>
    `${apps[run.snapshot.app_id]?.name ?? "App"} · ${robots.find((x) => x.id === run.robot_id)?.name ?? "Robot"}`;
  const filteredRuns = runs.filter(
    (run) =>
      (!appFilter || run.snapshot.app_id === appFilter) &&
      (!robotFilter || run.robot_id === robotFilter) &&
      (!stateFilter || run.state === stateFilter),
  );
  return (
    <main className="app-workflow-page">
      <header className="app-workflow-heading">
        <div>
          <p className="app-eyebrow">Runs and activity</p>
          <h1>{runId ? "Run details" : "App Logbook"}</h1>
          <p>Run state, event history, output and durable App artifacts.</p>
        </div>
        {runId && (
          <button
            className="button-secondary"
            type="button"
            onClick={() => selectRun()}
          >
            All runs
          </button>
        )}
      </header>
      {error && (
        <p role="alert" className="app-feedback app-feedback-error">
          {error}
        </p>
      )}
      {selected ? (
        <section className="app-panel">
          <p className="app-eyebrow">{selected.state}</p>
          <h2>{title(selected)}</h2>
          <p>
            Run <code>{selected.id}</code> · release{" "}
            <code>{selected.snapshot.revision_id}</code>
          </p>
          <p>
            <a href={resourcePath("robots", selected.robot_id, organizationId)}>
              Open{" "}
              {robots.find((robot) => robot.id === selected.robot_id)?.name ??
                "robot"}{" "}
              ↗
            </a>
          </p>
          <pre className="app-output">{output || "Loading output…"}</pre>
          <h3>Artifacts</h3>
          {artifacts.map((file) => (
            <p key={file.artifact_id}>
              <a
                href={`/api/v1/app-runs/${runId}/artifacts/${file.artifact_id}/content`}
                onClick={async (event) => {
                  event.preventDefault();
                  try {
                    const response = await apiFetch(
                      token,
                      `/v1/app-runs/${runId}/artifacts/${file.artifact_id}/content`,
                    );
                    const url = URL.createObjectURL(await response.blob());
                    const anchor = document.createElement("a");
                    anchor.href = url;
                    anchor.download = file.path.split("/").pop() ?? "artifact";
                    anchor.click();
                    URL.revokeObjectURL(url);
                  } catch (cause) {
                    setError(
                      cause instanceof Error
                        ? cause.message
                        : "Download failed.",
                    );
                  }
                }}
              >
                {file.path}
              </a>{" "}
              · {file.size} bytes
            </p>
          ))}
          {artifacts.length === 0 && <p>No artifacts.</p>}
          <h3>Events</h3>
          <ol className="app-event-list">
            {events
              .filter((e) => e.run_id === selected.id)
              .map((e) => (
                <li key={e.event_id}>
                  <time>{new Date(e.occurred_at).toLocaleString()}</time>
                  <strong>{e.type}</strong>
                  <span>
                    {typeof e.payload.message === "string"
                      ? e.payload.message
                      : JSON.stringify(e.payload)}
                  </span>
                </li>
              ))}
          </ol>
        </section>
      ) : (
        <div className="app-logbook-layout">
          <section className="app-panel">
            <h2>Runs</h2>
            <div className="app-logbook-filters">
              <label className="app-field">
                App
                <select
                  value={appFilter}
                  onChange={(event) => setAppFilter(event.target.value)}
                >
                  <option value="">All Apps</option>
                  {[...new Set(runs.map((run) => run.snapshot.app_id))].map(
                    (id) => (
                      <option key={id} value={id}>
                        {apps[id]?.name ?? id}
                      </option>
                    ),
                  )}
                </select>
              </label>
              <label className="app-field">
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
              <label className="app-field">
                State
                <select
                  value={stateFilter}
                  onChange={(event) => setStateFilter(event.target.value)}
                >
                  <option value="">All states</option>
                  {[...new Set(runs.map((run) => run.state))].map((state) => (
                    <option key={state}>{state}</option>
                  ))}
                </select>
              </label>
            </div>
            {filteredRuns.length ? (
              [...filteredRuns]
                .sort((a, b) => b.requested_at.localeCompare(a.requested_at))
                .map((run) => (
                  <button
                    className="app-logbook-row"
                    type="button"
                    key={run.id}
                    onClick={() => selectRun(run.id)}
                  >
                    <span>
                      <strong>{title(run)}</strong>
                      <small>
                        {new Date(run.requested_at).toLocaleString()} · {run.id}
                      </small>
                    </span>
                    <b>{run.state}</b>
                  </button>
                ))
            ) : (
              <p>
                {runs.length
                  ? "No runs match these filters."
                  : "No App runs recorded yet."}
              </p>
            )}
          </section>
          <section className="app-panel">
            <h2>Recent activity</h2>
            {[...events]
              .sort((a, b) => b.received_at.localeCompare(a.received_at))
              .filter(
                (event) =>
                  (!appFilter || event.app_id === appFilter) &&
                  (!robotFilter || event.robot_id === robotFilter),
              )
              .slice(0, 100)
              .map((event) => (
                <article className="app-event-row" key={event.event_id}>
                  <time>{new Date(event.occurred_at).toLocaleString()}</time>
                  <strong>{event.type}</strong>
                  <span>
                    {(event.payload.message as string) ??
                      event.app_id ??
                      "App event"}
                  </span>
                </article>
              ))}
          </section>
        </div>
      )}
    </main>
  );
}
