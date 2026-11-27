import { Folder } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { StudioDialog } from "@/components/StudioDialog";
import { ArtifactThumbnail } from "@/features/files/ArtifactThumbnail";
import { type AppRun, readJson, type Token, withQuery } from "@/lib/apps";
import { isPreviewable, verifiedArtifact } from "@/lib/artifacts";
import { resourcePath } from "@/lib/location";
import { runStateLabel } from "@/views/RunStatus";
import { StudioSection } from "@/views/StudioPage";

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
  robots,
  runs,
  token,
  fixedRunId,
  live = false,
}: {
  appId: string;
  organizationId: string;
  installationId?: string;
  robotName?: string;
  robots: { id: string; name: string }[];
  runs: AppRun[];
  token: Token;
  fixedRunId?: string;
  live?: boolean;
}) {
  const [rows, setRows] = useState<AppArtifact[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [runFilter, setRunFilter] = useState(fixedRunId ?? "");
  const [pathPrefix, setPathPrefix] = useState("");
  const [pathPrefixInput, setPathPrefixInput] = useState("");
  const [folderPrefix, setFolderPrefix] = useState("");
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [downloading, setDownloading] = useState("");
  const [preview, setPreview] = useState<{
    file: AppArtifact;
    url: string;
  } | null>(null);
  const mounted = useRef(false);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);
  useEffect(
    () => () => {
      if (preview) URL.revokeObjectURL(preview.url);
    },
    [preview],
  );
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
      run_id: fixedRunId || runFilter || undefined,
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
    fixedRunId,
    organizationId,
    installationId,
    runFilter,
    pathPrefix,
    folderPrefix,
    cursor,
    refresh,
    token,
  ]);

  useEffect(() => {
    if (!live || cursor) return;
    const timer = window.setInterval(() => {
      if (document.visibilityState === "visible")
        setRefresh((value) => value + 1);
    }, 3000);
    return () => window.clearInterval(timer);
  }, [live, cursor]);

  async function download(file: AppArtifact, showPreview = false) {
    setDownloading(file.artifact_id);
    setError("");
    try {
      const blob = await verifiedArtifact(token, file);
      const url = URL.createObjectURL(blob);
      if (showPreview) {
        if (!mounted.current) {
          URL.revokeObjectURL(url);
          return;
        }
        setPreview({ file, url });
        return;
      }
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
  const hasLocalFilters = Boolean(
    (!fixedRunId && runFilter) || pathPrefix || folderPrefix,
  );
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
    <StudioSection
      title={fixedRunId ? "Results" : "Published files"}
      titleId="app-files-title"
      className="app-files-panel"
      description={
        fixedRunId
          ? "Photos, reports and other output published by this run."
          : "Photos, reports and other output published across runs."
      }
      actions={
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
      }
    >
      {preview && (
        <StudioDialog
          title={preview.file.path.split("/").pop() ?? "Photo"}
          onClose={() => setPreview(null)}
        >
          <img
            className="app-photo-preview"
            src={preview.url}
            alt={`Published result: ${preview.file.path}`}
          />
          <p>
            {robots.find((item) => item.id === preview.file.robot_id)?.name ??
              "Robot"}{" "}
            ·{" "}
            {preview.file.published_at
              ? new Date(preview.file.published_at).toLocaleString()
              : "Published photo"}
          </p>
          <a href={resourcePath("runs", preview.file.run_id, organizationId)}>
            Open source run →
          </a>
          <button
            type="button"
            className="button-secondary"
            onClick={() => void download(preview.file)}
          >
            Download
          </button>
        </StudioDialog>
      )}
      {!fixedRunId && (
        <form
          className="studio-filter-bar studio-file-filters"
          onSubmit={(event) => {
            event.preventDefault();
            setRows([]);
            setCursor(null);
            setNextCursor(null);
            setFolderPrefix("");
            setPathPrefix(pathPrefixInput.trim());
            setRefresh((value) => value + 1);
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
                    · {runStateLabel(run.state)} ·{" "}
                    {new Date(run.requested_at).toLocaleString()}
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
          {pathPrefix && !pathPrefix.endsWith("/") && (
            <p className="app-files-scope">
              Add a trailing / to browse folders under this prefix.
            </p>
          )}
        </form>
      )}
      {!loading && !error && (
        <p className="studio-result-count">
          {rows.length} loaded file{rows.length === 1 ? "" : "s"}
          {nextCursor ? " · More files available" : ""}
        </p>
      )}
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
          {canBrowseFolders &&
            (!fixedRunId || folderCounts.size > 0 || folderPrefix) && (
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
                    <strong className="studio-folder-label">
                      <Folder size={20} strokeWidth={1.5} aria-hidden="true" />
                      {name}/
                    </strong>
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
            <div className="studio-files-grid">
              {visibleFiles.map((file) => {
                const run = runs.find((item) => item.id === file.run_id);
                const robot = robots.find((item) => item.id === file.robot_id);
                return (
                  <article
                    className={
                      isPreviewable(file)
                        ? "studio-file-preview studio-surface overflow-hidden"
                        : "studio-file-entry"
                    }
                    key={file.artifact_id}
                  >
                    {isPreviewable(file) && (
                      <button
                        type="button"
                        className="block w-full"
                        aria-label={`Preview ${file.path}`}
                        onClick={() => void download(file, true)}
                      >
                        <ArtifactThumbnail file={file} token={token} />
                      </button>
                    )}
                    <div className="flex flex-wrap items-center justify-between gap-3 p-4">
                      <div className="w-full min-w-0 space-y-1 text-sm">
                        <strong
                          className="block truncate font-mono text-sm"
                          title={file.path}
                        >
                          {file.path.split("/").pop()}
                        </strong>
                        <small className="block text-xs text-muted-foreground">
                          {robot?.name ?? "Robot"} ·{" "}
                          {run ? runStateLabel(run.state) : "Run"} ·{" "}
                          {file.published_at
                            ? new Date(file.published_at).toLocaleString()
                            : "publication time unavailable"}{" "}
                          · {displaySize(file.size)}
                        </small>
                        <a
                          className="block text-xs text-signal-text"
                          href={resourcePath(
                            "runs",
                            file.run_id,
                            organizationId,
                          )}
                        >
                          View run ↗
                        </a>
                        <details className="break-all text-xs text-muted-foreground">
                          <summary>Technical details</summary>
                          <p>{file.path}</p>
                          <code>SHA-256 {file.sha256}</code>
                          <br />
                          <code>Artifact {file.artifact_id}</code>
                          <br />
                          <code>Run {file.run_id}</code>
                        </details>
                      </div>
                      {isPreviewable(file) && (
                        <button
                          className="button-secondary"
                          type="button"
                          disabled={downloading === file.artifact_id}
                          onClick={() => void download(file, true)}
                        >
                          Preview
                        </button>
                      )}
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
                    </div>
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
    </StudioSection>
  );
}
