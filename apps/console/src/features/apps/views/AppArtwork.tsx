import { Blocks, Camera } from "lucide-react";
import { useState } from "react";
import type { App } from "@/lib/apps";
import { isCameraSnapshotLoop } from "./StoreHeaderImage";

export function appArtwork(app: Pick<App, "app_key" | "name">) {
  const name = `${app.app_key ?? ""} ${app.name}`.toLowerCase();
  if (name.includes("robot-check") || name.includes("robot check"))
    return "/app-artwork/robot-check.png";
  return undefined;
}
export function AppArtwork({
  app,
  className = "h-16 w-16",
}: {
  app: Pick<App, "app_key" | "name">;
  className?: string;
}) {
  const src = appArtwork(app);
  const [failedSource, setFailedSource] = useState<string>();
  if (isCameraSnapshotLoop(app))
    return (
      <span className={`${className} studio-camera-icon`} aria-hidden="true">
        <Camera strokeWidth={1.75} />
      </span>
    );
  return src && failedSource !== src ? (
    <img
      src={src}
      alt=""
      onError={() => setFailedSource(src)}
      className={`${className} shrink-0 border border-border object-cover`}
    />
  ) : (
    <div
      aria-hidden="true"
      className={`${className} flex shrink-0 items-center justify-center border border-border bg-muted font-display text-2xl`}
    >
      <Blocks size={28} strokeWidth={1.5} aria-hidden="true" />
    </div>
  );
}
