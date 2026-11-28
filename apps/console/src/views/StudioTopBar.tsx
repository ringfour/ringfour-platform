import { Settings, ShieldCheck, UserRound } from "lucide-react";
import { type StudioView, studioPath } from "@/lib/location";

const names: Record<string, string> = {
  overview: "Home",
  apps: "Apps",
  store: "Hub",
  robots: "Robots",
  logbook: "Logbook",
  timeline: "Timeline",
  identity: "Organizations",
  health: "System health",
  profile: "My profile",
  unavailable: "Studio",
};

export function StudioTopBar({
  workspace,
  workspaces,
  view,
  onWorkspaceChange,
}: {
  workspace: string;
  workspaces: { id: string; name: string; role?: string }[];
  view: StudioView | "unavailable";
  onWorkspaceChange?: (id: string) => void;
}) {
  const activeWorkspace = workspaces.find((item) => item.id === workspace);
  return (
    <nav className="studio-topbar" aria-label="Studio toolbar">
      <span className="studio-topbar-context">{names[view]}</span>
      <div className="studio-topbar-controls">
        <label className="studio-organization-picker">
          <span className="studio-organization-label">Organization</span>
          <select
            className="studio-field"
            value={workspace}
            disabled={!workspaces.length || !onWorkspaceChange}
            onChange={(event) => onWorkspaceChange?.(event.target.value)}
          >
            {!activeWorkspace && (
              <option value={workspace}>
                {workspace
                  ? "Organization unavailable"
                  : "Choose an organization"}
              </option>
            )}
            {workspaces.map((item) => (
              <option key={item.id} value={item.id}>
                {item.name}
              </option>
            ))}
          </select>
        </label>
        <div className="studio-topbar-utilities">
          {(
            [
              { label: "Organizations", view: "identity", icon: Settings },
              { label: "System health", view: "health", icon: ShieldCheck },
              { label: "My profile", view: "profile", icon: UserRound },
            ] as const
          ).map(({ label, view: target, icon: Icon }) => (
            <a
              key={target}
              href={studioPath(target, workspace)}
              aria-label={label}
              title={label}
              aria-current={view === target ? "page" : undefined}
            >
              <Icon size={20} aria-hidden="true" />
            </a>
          ))}
        </div>
      </div>
    </nav>
  );
}
