import type { App } from "@/lib/apps";
export function Publisher({ app }: { app: App }) {
  return (
    <span className="app-publisher">
      {app.publisher_type === "ringfour"
        ? "Ringfour · Official"
        : `${app.publisher_name ?? "Workspace publisher"} · Workspace App`}
    </span>
  );
}
