import { Image } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import type { Token } from "@/lib/apps";
import { type ArtifactReceipt, verifiedArtifact } from "@/lib/artifacts";
/** Load visible raster results only; refuse mismatched receipts and release blob URLs. */
export function ArtifactThumbnail({
  file,
  token,
  fit = "cover",
}: {
  file: ArtifactReceipt;
  token: Token;
  fit?: "cover" | "contain";
}) {
  const host = useRef<HTMLDivElement>(null);
  const [visible, setVisible] = useState(false);
  const [url, setUrl] = useState("");
  const [error, setError] = useState("");
  useEffect(() => {
    if (!host.current || typeof IntersectionObserver === "undefined") return;
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((entry) => entry.isIntersecting)) {
          setVisible(true);
          observer.disconnect();
        }
      },
      { rootMargin: "100px" },
    );
    observer.observe(host.current);
    return () => observer.disconnect();
  }, []);
  const { artifact_id, run_id, path, size, sha256, content_type } = file;
  useEffect(() => {
    if (!visible) return;
    const controller = new AbortController();
    let live = true;
    let objectUrl = "";
    setUrl("");
    setError("");
    void verifiedArtifact(
      token,
      { artifact_id, run_id, path, size, sha256, content_type },
      controller.signal,
    )
      .then((blob) => {
        if (!live) return;
        objectUrl = URL.createObjectURL(blob);
        setUrl(objectUrl);
      })
      .catch((cause) => {
        if (live)
          setError(
            cause instanceof Error ? cause.message : "Preview unavailable",
          );
      });
    return () => {
      live = false;
      controller.abort();
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [visible, artifact_id, run_id, path, size, sha256, content_type, token]);
  return (
    <div
      ref={host}
      className="flex aspect-[4/3] w-full items-center justify-center overflow-hidden bg-background"
    >
      {url ? (
        <img
          src={url}
          alt={`Published photo: ${file.path}`}
          className={`h-full w-full ${fit === "contain" ? "object-contain" : "object-cover"}`}
        />
      ) : (
        <span className="flex max-w-full flex-col items-center gap-2 p-4 text-center text-xs text-muted-foreground">
          <Image className="h-6 w-6" />
          {error || "Photo preview"}
        </span>
      )}
    </div>
  );
}
