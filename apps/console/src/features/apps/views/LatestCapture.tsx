import { useEffect, useState } from "react";
import { StudioTime } from "@/components/StudioTime";
import { ArtifactThumbnail } from "@/features/files/ArtifactThumbnail";
import { readJson, type Token, withQuery } from "@/lib/apps";
import { type ArtifactReceipt, isPreviewable } from "@/lib/artifacts";

type Capture = ArtifactReceipt & { published_at?: string | null };

export function LatestCapture({
  appId,
  organizationId,
  installationId,
  token,
  running,
  runId,
}: {
  appId: string;
  organizationId: string;
  installationId: string;
  token: Token;
  running: boolean;
  runId?: string;
}) {
  const [capture, setCapture] = useState<Capture>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);

  useEffect(() => {
    // A run transition also refreshes the final photo after the device stops.
    void runId;
    let active = true;
    let timer: number | undefined;
    async function load() {
      try {
        let cursor: string | undefined;
        let latest: Capture | undefined;
        do {
          const page: { items: Capture[]; next_cursor?: string | null } =
            await readJson(
              token,
              withQuery(`/v1/apps/${appId}/artifacts`, {
                organization_id: organizationId,
                installation_id: installationId,
                limit: "16",
                cursor,
              }),
            );
          latest = page.items.find(isPreviewable);
          cursor = page.next_cursor ?? undefined;
        } while (active && !latest && cursor);

        if (active) {
          setCapture(latest);
          setError(false);
        }
      } catch {
        if (active) setError(true);
      } finally {
        if (active) {
          setLoading(false);
          if (running)
            timer = window.setTimeout(load, document.hidden ? 30000 : 5000);
        }
      }
    }
    void load();
    return () => {
      active = false;
      window.clearTimeout(timer);
    };
  }, [appId, organizationId, installationId, token, running, runId]);

  return (
    <>
      {capture && (
        <figure className="app-latest-capture">
          <ArtifactThumbnail
            key={capture.artifact_id}
            file={capture}
            token={token}
            fit="contain"
          />
          <figcaption className="studio-row-detail">
            {capture.path}
            {capture.published_at && (
              <>
                {" "}
                · <StudioTime value={capture.published_at} />
              </>
            )}
          </figcaption>
        </figure>
      )}
      {error ? (
        <p className="app-muted" role="status">
          Could not refresh the latest capture.
        </p>
      ) : (
        !capture && (
          <p className="app-muted" role="status">
            {loading
              ? "Looking for the latest capture…"
              : "No photos published yet."}
          </p>
        )
      )}
    </>
  );
}
