import { useCallback, useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { apiFetch } from "@/lib/api";

export type Artifact = {
  artifact_id: string;
  path: string;
  size: number;
  sha256: string;
  content_type: "image/jpeg" | "image/png" | "application/octet-stream";
  state: "pending" | "uploading" | "available" | "failed";
};

const errorMessage = (error: unknown) =>
  error instanceof Error ? error.message : "Request failed";
const fileName = (path: string) => path.split("/").at(-1) || path;
const formatSize = (size: number) => {
  if (size < 1024) return `${size.toLocaleString()} B`;
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KB`;
  return `${(size / (1024 * 1024)).toFixed(1)} MB`;
};

export function ArtifactCard({
  artifact,
  executionId,
  getToken,
  compact = false,
  gallery,
}: {
  artifact: Artifact;
  executionId: string;
  getToken: () => Promise<string>;
  compact?: boolean;
  gallery?: {
    open: boolean;
    select: (id: string) => void;
    previous?: string;
    next?: string;
  };
}) {
  const [preview, setPreview] = useState("");
  const [error, setError] = useState("");
  const [localOpen, setLocalOpen] = useState(false);
  const lightboxOpen = gallery?.open ?? localOpen;
  const selectImage = gallery?.select;
  const setLightboxOpen = useCallback(
    (open: boolean) => {
      if (selectImage) selectImage(open ? artifact.artifact_id : "");
      else setLocalOpen(open);
    },
    [selectImage, artifact.artifact_id],
  );
  const closeButton = useRef<HTMLButtonElement>(null);
  const isImage =
    artifact.content_type === "image/jpeg" ||
    artifact.content_type === "image/png";
  const fetchBlob = useCallback(async () => {
    const response = await apiFetch(
      getToken,
      `/v1/executions/${encodeURIComponent(executionId)}/artifacts/content?path=${encodeURIComponent(artifact.path)}`,
    );
    return response.blob();
  }, [artifact.path, executionId, getToken]);

  useEffect(() => {
    if (!isImage || artifact.state !== "available") return;
    let cancelled = false;
    let objectUrl = "";
    fetchBlob()
      .then((blob) => {
        if (cancelled) return;
        objectUrl = URL.createObjectURL(blob);
        setPreview(objectUrl);
        setError("");
      })
      .catch((reason) => {
        if (!cancelled) setError(errorMessage(reason));
      });
    return () => {
      cancelled = true;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [artifact.state, fetchBlob, isImage]);

  useEffect(() => {
    if (!lightboxOpen) return;
    const previousFocus = document.activeElement;
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    closeButton.current?.focus();
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === "Escape") setLightboxOpen(false);
      if (event.key === "Tab") {
        const controls = closeButton.current
          ?.closest('[role="dialog"]')
          ?.querySelectorAll<HTMLButtonElement>("button");
        const first = controls?.[0];
        const last = controls?.[controls.length - 1];
        if (event.shiftKey && document.activeElement === first) {
          event.preventDefault();
          last?.focus();
        } else if (!event.shiftKey && document.activeElement === last) {
          event.preventDefault();
          first?.focus();
        }
      }
    };
    window.addEventListener("keydown", closeOnEscape);
    return () => {
      window.removeEventListener("keydown", closeOnEscape);
      document.body.style.overflow = previousOverflow;
      if (previousFocus instanceof HTMLElement && previousFocus.isConnected) {
        previousFocus.focus();
      }
    };
  }, [lightboxOpen, setLightboxOpen]);

  const download = async () => {
    try {
      const objectUrl = URL.createObjectURL(await fetchBlob());
      const link = document.createElement("a");
      link.href = objectUrl;
      link.download = fileName(artifact.path);
      link.click();
      window.setTimeout(() => URL.revokeObjectURL(objectUrl), 0);
    } catch (reason) {
      setError(errorMessage(reason));
    }
  };

  return (
    <article
      className={`logbook-artifact${compact ? " logbook-artifact-inline" : ""}`}
      data-state={artifact.state}
      id={compact ? undefined : `artifact-${artifact.artifact_id}`}
    >
      {preview ? (
        <button
          className="logbook-artifact-preview"
          type="button"
          onClick={() => setLightboxOpen(true)}
          aria-label={`Open preview for ${fileName(artifact.path)}`}
        >
          <img
            src={preview}
            alt={`Mission artifact ${artifact.path}`}
            loading="lazy"
          />
          <span aria-hidden="true">View image</span>
        </button>
      ) : (
        <div
          className="logbook-artifact-placeholder"
          data-state={artifact.state}
        >
          <span>{isImage ? "Image preview" : "File"}</span>
          <small>{artifact.state}</small>
        </div>
      )}
      <div className="logbook-artifact-info">
        <div className="logbook-artifact-title-row">
          <strong title={artifact.path}>{fileName(artifact.path)}</strong>
          <span className="logbook-artifact-state" data-state={artifact.state}>
            {artifact.state}
          </span>
        </div>
        <span className="logbook-artifact-path" title={artifact.path}>
          {artifact.path}
        </span>
        <span className="logbook-artifact-meta">
          {artifact.content_type} <span aria-hidden="true">·</span>{" "}
          {formatSize(artifact.size)}
        </span>
      </div>
      <div className="logbook-artifact-actions">
        {preview && (
          <button
            type="button"
            className="logbook-artifact-view"
            onClick={() => setLightboxOpen(true)}
          >
            Preview
          </button>
        )}
        <button
          type="button"
          className="logbook-artifact-download"
          onClick={() => void download()}
          disabled={artifact.state !== "available"}
        >
          Download
        </button>
      </div>
      {error && (
        <p role="alert" className="logbook-artifact-error">
          Preview unavailable: {error}
        </p>
      )}
      {lightboxOpen &&
        preview &&
        createPortal(
          <div
            className="logbook-artifact-lightbox"
            role="dialog"
            aria-modal="true"
            aria-label={`Preview ${fileName(artifact.path)}`}
          >
            <div className="logbook-artifact-lightbox-panel">
              <header>
                <div>
                  <span>Execution image</span>
                  <strong>{fileName(artifact.path)}</strong>
                </div>
                <button
                  ref={closeButton}
                  type="button"
                  onClick={() => setLightboxOpen(false)}
                  aria-label="Close image preview"
                >
                  ×
                </button>
              </header>
              <img
                src={preview}
                alt={`Full-size mission artifact ${artifact.path}`}
              />
              <footer>
                {gallery?.previous && (
                  <button
                    type="button"
                    onClick={() => gallery.select(gallery.previous!)}
                  >
                    Previous image
                  </button>
                )}
                {gallery?.next && (
                  <button
                    type="button"
                    onClick={() => gallery.select(gallery.next!)}
                  >
                    Next image
                  </button>
                )}
                <span>
                  {artifact.path} · {formatSize(artifact.size)}
                </span>
                <button
                  type="button"
                  className="logbook-artifact-download"
                  onClick={() => void download()}
                >
                  Download image
                </button>
              </footer>
            </div>
          </div>,
          document.body,
        )}
    </article>
  );
}

export function ArtifactGallery({
  artifacts,
  executionId,
  getToken,
  error,
  loading,
  pollIntervalMs = 3000,
  sectionIndex,
}: {
  artifacts: Artifact[] | null;
  executionId: string;
  getToken: () => Promise<string>;
  error?: string;
  loading?: boolean;
  pollIntervalMs?: number;
  sectionIndex?: string;
}) {
  const [openId, setOpenId] = useState("");
  const images = (artifacts ?? []).filter(
    (artifact) =>
      artifact.content_type === "image/jpeg" ||
      artifact.content_type === "image/png",
  );
  const available = images.filter((image) => image.state === "available");
  const index = available.findIndex((item) => item.artifact_id === openId);
  return (
    <section
      className={`logbook-artifacts${sectionIndex ? " logbook2-section" : ""}`}
      aria-label="Execution gallery"
    >
      <header className="logbook-artifacts-header">
        <div className="logbook-artifacts-heading">
          {sectionIndex && (
            <span className="logbook2-section-index">{sectionIndex}</span>
          )}
          <div>
            <span className="logbook-artifacts-kicker">Execution evidence</span>
            <h2>Gallery</h2>
          </div>
        </div>
        <div className="logbook-artifacts-summary">
          {artifacts && <span>{artifacts.length} files</span>}
          {artifacts && images.length > 0 && (
            <span>
              {images.length} image{images.length === 1 ? "" : "s"}
            </span>
          )}
          <span
            className={pollIntervalMs ? "logbook-artifacts-live" : undefined}
          >
            {pollIntervalMs
              ? `Refreshing every ${Math.round(pollIntervalMs / 1000)}s`
              : "Recorded evidence"}
          </span>
        </div>
      </header>
      {error && (
        <p role="alert" className="notice-error">
          Artifacts unavailable: {error}
        </p>
      )}
      {loading || artifacts === null ? (
        <p className="logbook-artifacts-message">Loading gallery…</p>
      ) : artifacts.length === 0 ? (
        <p className="logbook-artifacts-message">
          Photos and files will appear here as the mission publishes them.
        </p>
      ) : (
        <div className="logbook-artifact-grid">
          {artifacts.map((artifact) => (
            <ArtifactCard
              key={artifact.artifact_id}
              gallery={{
                open: openId === artifact.artifact_id,
                select: setOpenId,
                previous: available[index - 1]?.artifact_id,
                next: available[index + 1]?.artifact_id,
              }}
              artifact={artifact}
              executionId={executionId}
              getToken={getToken}
            />
          ))}
        </div>
      )}
    </section>
  );
}
