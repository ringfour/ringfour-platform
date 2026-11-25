import { useEffect, useState } from "react";
import { apiFetch } from "@/lib/api";
import { type AppRun, readJson, type Token, withQuery } from "@/lib/apps";
import { resourcePath } from "@/lib/location";

type AppArtifact = {
  artifact_id: string;
  run_id: string;
  installation_id: string;
  robot_id: string;
  path: string;
  size: number;
  sha256: string;
  content_type: string;
  published_at: string | null;
};
type ArtifactPage = { items: AppArtifact[]; next_cursor: string | null };

function displaySize(bytes: number) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export function AppFiles({
  appId,
  organizationId,
  installationId,
  robotName,
  robots,
  runs,
  token,
}: {
  appId: string;
  organizationId: string;
  installationId?: string;
  robotName?: string;
  robots: { id: string; name: string }[];
  runs: AppRun[];
  token: Token;
}) {
  const [rows, setRows] = useState<AppArtifact[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [runFilter, setRunFilter] = useState("");
  const [pathPrefix, setPathPrefix] = useState("");
  const [pathPrefixInput, setPathPrefixInput] = useState("");
  const [folderPrefix, setFolderPrefix] = useState("");
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [downloading, setDownloading] = useState("");
  const [refresh, setRefresh] = useState(0);

  useEffect(() => {
    void refresh;
    let live = true;
    setLoading(true);
    setError("");
    const requestPrefix = folderPrefix
      ? `${pathPrefix}${pathPrefix && !pathPrefix.endsWith("/") ? "/" : ""}${folderPrefix}`
      : pathPrefix;
    const path = withQuery(`/v1/apps/${appId}/artifacts`, {
      organization_id: organizationId,
      installation_id: installationId,
      run_id: runFilter || undefined,
      path_prefix: requestPrefix.trim() || undefined,
      cursor: cursor ?? undefined,
      limit: "50",
    });
    void readJson<ArtifactPage>(token, path)
      .then((page) => {
        if (!live) return;
        setRows((current) =>
          cursor ? [...current, ...page.items] : page.items,
        );
        setNextCursor(page.next_cursor);
      })
      .catch((cause) => {
        if (live)
          setError(
            cause instanceof Error
              ? cause.message
              : "Could not load App files.",
          );
      })
      .finally(() => {
        if (live) setLoading(false);
      });
    return () => {
      live = false;
    };
  }, [
    appId,
    organizationId,
    installationId,
    runFilter,
    pathPrefix,
    folderPrefix,
    cursor,
    refresh,
    token,
  ]);

  async function download(file: AppArtifact) {
    setDownloading(file.artifact_id);
    setError("");
    try {
      const response = await apiFetch(
        token,
        `/v1/app-runs/${file.run_id}/artifacts/${file.artifact_id}/content`,
      );
      const blob = await response.blob();
      if (blob.size !== file.size)
        throw new Error(
          "Downloaded file size does not match its published receipt.",
        );
      const digest = Array.from(
        new Uint8Array(
          await crypto.subtle.digest("SHA-256", await blob.arrayBuffer()),
        ),
      )
        .map((value) => value.toString(16).padStart(2, "0"))
        .join("");
      if (digest !== file.sha256)
        throw new Error(
          "Downloaded file hash does not match its published receipt.",
        );
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement("a");
      anchor.href = url;
      anchor.download = file.path.split("/").pop() || "artifact";
      anchor.click();
      window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Download failed.");
    } finally {
      setDownloading("");
    }
  }

  const canBrowseFolders = !pathPrefix || pathPrefix.endsWith("/");
  const displayPrefix = canBrowseFolders ? `${pathPrefix}${folderPrefix}` : "";
  const folderCounts = new Map<string, number>();
  const visibleFiles = rows.filter((file) => {
    const relative =
      displayPrefix && file.path.startsWith(displayPrefix)
        ? file.path.slice(displayPrefix.length)
        : file.path;
    const separator = relative.indexOf("/");
    if (separator < 0) return true;
    const name = relative.slice(0, separator);
    folderCounts.set(name, (folderCounts.get(name) ?? 0) + 1);
    return false;
  });
  const hasLocalFilters = Boolean(runFilter || pathPrefix || folderPrefix);
  const hasFilters = Boolean(installationId || hasLocalFilters);
  const folderParts = folderPrefix.split("/").filter(Boolean);
  function resetFilters() {
    setRunFilter("");
    setPathPrefix("");
    setPathPrefixInput("");
    setFolderPrefix("");
    setRows([]);
    setCursor(null);
    setNextCursor(null);
  }

  return (
    <section
      className="app-panel app-files-panel"
      aria-labelledby="app-files-title"
    >
      <header className="app-files-heading">
        <div>
          <p className="app-eyebrow">Files</p>
          <h2 id="app-files-title">
            Published files{robotName ? ` · ${robotName}` : ""}
          </h2>
          <p>
            Confirmed App outputs across runs. These are published artifacts,
            not an editable App disk.
          </p>
        </div>
        <button
          className="button-secondary"
          type="button"
          onClick={() => {
            setCursor(null);
            setRefresh((value) => value + 1);
          }}
        >
          Refresh
        </button>
      </header>
      <form
        className="app-files-filters"
        onSubmit={(event) => {
          event.preventDefault();
          setRows([]);
          setCursor(null);
          setNextCursor(null);
          setFolderPrefix("");
          setPathPrefix(pathPrefixInput.trim());
        }}
      >
        <label className="app-field" htmlFor="app-files-run">
          Run
          <select
            id="app-files-run"
            value={runFilter}
            onChange={(event) => {
              setRows([]);
              setCursor(null);
              setNextCursor(null);
              setRunFilter(event.target.value);
            }}
          >
            <option value="">All runs</option>
            {runs
              .filter(
                (run) =>
                  !installationId || run.installation_id === installationId,
              )
              .map((run) => (
                <option key={run.id} value={run.id}>
                  {robots.find((robot) => robot.id === run.robot_id)?.name ??
                    "Robot"}{" "}
                  · {run.state} · {new Date(run.requested_at).toLocaleString()}
                </option>
              ))}
          </select>
        </label>
        <label className="app-field" htmlFor="app-files-path">
          Folder or path prefix
          <input
            id="app-files-path"
            value={pathPrefixInput}
            onChange={(event) => setPathPrefixInput(event.target.value)}
            placeholder="reports/"
          />
        </label>
        <div className="app-file-filter-actions">
          <button className="button-primary" type="submit">
            Apply filters
          </button>
          {hasLocalFilters && (
            <button
              className="button-secondary"
              type="button"
              onClick={resetFilters}
            >
              Clear run and path filters
            </button>
          )}
        </div>
        <p className="app-files-scope">
          {installationId
            ? `Showing ${robotName ?? "the selected robot"}.`
            : "Showing files from all App installations in this workspace."}
          {pathPrefix && !pathPrefix.endsWith("/") && (
            <> Add a trailing / to browse folders under this prefix.</>
          )}
        </p>
      </form>
      {error && (
        <p role="alert" className="app-feedback app-feedback-error">
          {error}
        </p>
      )}
      {error && !loading && rows.length === 0 ? (
        <div className="app-library-empty">
          <h3>Published files could not be loaded</h3>
          <p>Check the connection, then retry this listing.</p>
          <button
            className="button-secondary"
            type="button"
            onClick={() => setRefresh((value) => value + 1)}
          >
            Try again
          </button>
        </div>
      ) : rows.length > 0 ? (
        <div className="app-files-browser" aria-live="polite">
          {canBrowseFolders && (
            <nav className="app-files-breadcrumbs" aria-label="File folders">
              {pathPrefix && (
                <span className="app-files-base-prefix">{pathPrefix}</span>
              )}
              <button
                type="button"
                aria-current={folderPrefix ? undefined : "page"}
                onClick={() => {
                  setRows([]);
                  setCursor(null);
                  setNextCursor(null);
                  setFolderPrefix("");
                }}
              >
                Files
              </button>
              {folderParts.map((part, index) => {
                const prefix = `${folderParts.slice(0, index + 1).join("/")}/`;
                return (
                  <button
                    type="button"
                    key={prefix}
                    aria-current={
                      index === folderParts.length - 1 ? "page" : undefined
                    }
                    onClick={() => {
                      setRows([]);
                      setCursor(null);
                      setNextCursor(null);
                      setFolderPrefix(prefix);
                    }}
                  >
                    {part}
                  </button>
                );
              })}
            </nav>
          )}
          {folderCounts.size > 0 && (
            <section
              className="app-files-folders"
              aria-labelledby="app-files-folders-title"
            >
              <h3 id="app-files-folders-title">Folders</h3>
              {[...folderCounts]
                .sort(([left], [right]) => left.localeCompare(right))
                .map(([name, count]) => (
                  <button
                    className="app-folder-row"
                    type="button"
                    key={name}
                    onClick={() => {
                      setRows([]);
                      setCursor(null);
                      setNextCursor(null);
                      setFolderPrefix(`${folderPrefix}${name}/`);
                    }}
                  >
                    <strong>{name}/</strong>
                    <span>
                      {count} loaded file{count === 1 ? "" : "s"}
                    </span>
                    <span aria-hidden="true">Open →</span>
                  </button>
                ))}
              {nextCursor && (
                <small>
                  Folder entries are derived from loaded files; load more to
                  discover additional folders.
                </small>
              )}
            </section>
          )}
          {visibleFiles.length > 0 && (
            <div className="app-files-list">
              {visibleFiles.map((file) => {
                const run = runs.find((item) => item.id === file.run_id);
                const robot = robots.find((item) => item.id === file.robot_id);
                return (
                  <article className="app-file-row" key={file.artifact_id}>
                    <div className="app-file-main">
                      <strong>{file.path.split("/").pop()}</strong>
                      <span>{file.path}</span>
                      <small>
                        {robot?.name ?? "Robot"} · {run?.state ?? "Run"} ·{" "}
                        {file.published_at
                          ? new Date(file.published_at).toLocaleString()
                          : "publication time unavailable"}{" "}
                        · {displaySize(file.size)}
                      </small>
                      <a
                        href={resourcePath("runs", file.run_id, organizationId)}
                      >
                        View run ↗
                      </a>
                      <details>
                        <summary>Technical details</summary>
                        <code>SHA-256 {file.sha256}</code>
                        <br />
                        <code>Artifact {file.artifact_id}</code>
                        <br />
                        <code>Run {file.run_id}</code>
                      </details>
                    </div>
                    <button
                      className="button-secondary"
                      type="button"
                      disabled={downloading === file.artifact_id}
                      onClick={() => void download(file)}
                    >
                      {downloading === file.artifact_id
                        ? "Checking…"
                        : "Download"}
                    </button>
                  </article>
                );
              })}
            </div>
          )}
        </div>
      ) : loading ? (
        <p role="status">Loading published files…</p>
      ) : (
        <div className="app-library-empty">
          <p className="app-eyebrow">Files</p>
          <h3>
            {hasFilters
              ? "No files match this robot and these filters"
              : "No published files yet"}
          </h3>
          <p>
            {hasFilters
              ? "Try a different robot, run or path prefix, or clear the filters."
              : "Published App outputs will appear here with their run and robot attribution."}
          </p>
          {hasLocalFilters && (
            <button
              className="button-secondary"
              type="button"
              onClick={resetFilters}
            >
              Clear run and path filters
            </button>
          )}
        </div>
      )}
      {nextCursor && (
        <button
          className="button-secondary"
          type="button"
          disabled={loading}
          onClick={() => setCursor(nextCursor)}
        >
          Load more files
        </button>
      )}
    </section>
  );
}
