import { useEffect, useState } from "react";
import { activeRun, readRunOutput } from "@/lib/app-history";
import type { AppRun, Token } from "@/lib/apps";

/** The pre-pivot split output panel, backed by App run output pages. */
export function AppRunOutput({
  run,
  token,
  refresh,
}: {
  run: AppRun;
  token: Token;
  refresh: number;
}) {
  const [output, setOutput] = useState<{
    stdout: string;
    stderr: string;
    truncated: boolean;
  } | null>(null);
  const [error, setError] = useState("");
  const [copyError, setCopyError] = useState("");
  const [follow, setFollow] = useState(true);
  const liveRun = activeRun(run);
  useEffect(() => {
    void refresh;
    let live = true;
    let timer: number | undefined;
    const load = async () => {
      try {
        const [stdout, stderr] = await Promise.all([
          readRunOutput(token, run.id, "stdout"),
          readRunOutput(token, run.id, "stderr"),
        ]);
        if (live) {
          setOutput({
            stdout: stdout.text,
            stderr: stderr.text,
            truncated: stdout.truncated || stderr.truncated,
          });
          setError("");
        }
      } catch (cause) {
        if (live)
          setError(
            cause instanceof Error ? cause.message : "Output unavailable",
          );
      } finally {
        if (live && liveRun && follow)
          timer = window.setTimeout(
            load,
            document.visibilityState === "visible" ? 1500 : 5000,
          );
      }
    };
    void load();
    return () => {
      live = false;
      window.clearTimeout(timer);
    };
  }, [token, run.id, liveRun, follow, refresh]);
  async function copy(stream: "stdout" | "stderr") {
    try {
      await navigator.clipboard.writeText(output?.[stream] ?? "");
      setCopyError("");
    } catch {
      setCopyError("Clipboard unavailable.");
    }
  }
  return (
    <section className="logbook2-section" aria-label="Run output" id="output">
      <header className="logbook2-section-heading">
        <div>
          <span className="logbook2-section-index">03</span>
          <h2>Run output</h2>
        </div>
        {output?.truncated && <span>Truncated</span>}
      </header>
      <div className="logbook-output-actions">
        {liveRun && (
          <button
            type="button"
            className="button-secondary"
            onClick={() => setFollow((value) => !value)}
          >
            {follow ? "Pause updates" : "Resume updates"}
          </button>
        )}
        {output &&
          (["stdout", "stderr"] as const).map((stream) => (
            <div key={stream}>
              <button
                type="button"
                className="text-action"
                onClick={() => void copy(stream)}
              >
                Copy {stream}
              </button>
              <button
                type="button"
                className="text-action"
                aria-label={`Download ${stream}`}
                onClick={() => {
                  const url = URL.createObjectURL(
                    new Blob([output[stream]], {
                      type: "text/plain;charset=utf-8",
                    }),
                  );
                  const link = document.createElement("a");
                  link.href = url;
                  link.download = `${run.id}-${stream}.txt`;
                  link.click();
                  window.setTimeout(() => URL.revokeObjectURL(url), 1000);
                }}
              >
                Download
              </button>
            </div>
          ))}
      </div>
      {copyError && (
        <p role="alert" className="logbook2-detail-error">
          {copyError}
        </p>
      )}
      {error && (
        <p role="alert" className="logbook2-detail-error">
          Output unavailable: {error}
          {output && " · Showing the last received output."}
        </p>
      )}
      {!output ? (
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
  );
}
