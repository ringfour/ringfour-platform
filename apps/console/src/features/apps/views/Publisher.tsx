import type { App } from "@/lib/apps";
export function Publisher({
  app,
  workspace,
}: {
  app: App;
  workspace?: string;
}) {
  const sameWorkspace = app.publisher_organization_id === workspace;
  const publisher = app.publisher_name ?? "Workspace publisher";
  return (
    <span className="app-publisher">
      {sameWorkspace
        ? `${app.publisher_user_name ?? "Workspace member"} · ${publisher}`
        : app.publisher_type === "ringfour"
          ? "Ringfour · Official"
          : `${publisher} · Workspace App`}
    </span>
  );
}
