import { useAuth0 } from "@auth0/auth0-react";
import { useState } from "react";

import { DashboardPage } from "@/components/DashboardPage";
import { FleetPage } from "@/components/FleetPage";
import { HealthPage } from "@/components/HealthPage";
import { IdentityPage } from "@/components/IdentityPage";
import { LogbookPage } from "@/components/LogbookPage";
import { LoginPage } from "@/components/LoginPage";
import { RecordsPage } from "@/components/RecordsPage";

type View =
  | "overview"
  | "robots"
  | "missions"
  | "deployments"
  | "identity"
  | "health"
  | "logbook";

const NAV: { id: View; label: string; icon: string }[] = [
  { id: "overview", label: "Overview", icon: "overview" },
  { id: "robots", label: "Fleet", icon: "fleet" },
  { id: "missions", label: "Missions", icon: "missions" },
  { id: "deployments", label: "Deployments", icon: "deployments" },
  { id: "logbook", label: "Logbook", icon: "logbook" },
  { id: "identity", label: "Workspace", icon: "workspace" },
  { id: "health", label: "System health", icon: "health" },
];

function Icon({ name }: { name: string }) {
  const paths: Record<string, React.ReactNode> = {
    overview: (
      <>
        <rect x="3" y="3" width="7" height="7" />
        <rect x="14" y="3" width="7" height="7" />
        <rect x="3" y="14" width="7" height="7" />
        <rect x="14" y="14" width="7" height="7" />
      </>
    ),
    fleet: (
      <>
        <rect x="4" y="7" width="16" height="11" rx="2" />
        <path d="M8 18v2m8-2v2M12 4v3M9 12h.01M15 12h.01M9 15h6" />
      </>
    ),
    missions: (
      <>
        <path d="M5 3h10l4 4v14H5z" />
        <path d="M15 3v5h4M8 12h8M8 16h8" />
      </>
    ),
    deployments: (
      <>
        <rect x="3" y="5" width="18" height="14" rx="1" />
        <path d="M8 12h8m-3-3 3 3-3 3" />
      </>
    ),
    logbook: (
      <>
        <path d="M5 3h14v18H5zM8 3v18M11 8h5M11 12h5M11 16h5" />
      </>
    ),
    workspace: (
      <>
        <circle cx="12" cy="8" r="3.5" />
        <path d="M5 20v-1a7 7 0 0 1 14 0v1" />
      </>
    ),
    health: (
      <>
        <path d="M3 12h4l2.2-6 4 12 2.2-6H21" />
        <rect x="2" y="2" width="20" height="20" rx="3" />
      </>
    ),
  };
  return (
    <svg
      aria-hidden="true"
      viewBox="0 0 24 24"
      width="19"
      height="19"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      {paths[name]}
    </svg>
  );
}

export default function App() {
  if (!import.meta.env.DEV) {
    return (
      <main className="mx-auto max-w-2xl p-6">
        <p>Ringfour Console is a development-only tool.</p>
      </main>
    );
  }
  return <DevConsole />;
}

function DevConsole() {
  const { isAuthenticated, isLoading, error } = useAuth0();
  const [view, setView] = useState<View>("overview");
  const [organizationId, setOrganizationId] = useState("");
  const [robotId, setRobotId] = useState<string | undefined>();
  const navigate = (next: View, robot?: string) => {
    setRobotId(robot);
    setView(next);
  };

  if (isLoading)
    return <main className="auth-wait">Loading your workspace…</main>;
  if (error)
    return (
      <main className="auth-wait">
        <p role="alert" className="text-danger">
          Authentication error: {error.message}
        </p>
      </main>
    );
  if (!isAuthenticated) return <LoginPage />;

  return (
    <div className="console-shell">
      <aside className="console-sidebar">
        <div className="console-brand">
          <img src="/ringfour-logo.svg" alt="" />
          <strong>ringfour</strong>
        </div>
        <nav className="console-nav" aria-label="Main navigation">
          {NAV.map((item) => (
            <button
              key={item.id}
              type="button"
              aria-current={view === item.id ? "page" : undefined}
              onClick={() => navigate(item.id)}
            >
              <Icon name={item.icon} />
              <span>{item.label}</span>
              {view === item.id && (
                <span className="nav-active-dot" aria-hidden="true" />
              )}
            </button>
          ))}
        </nav>
      </aside>
      <div className="console-body">
        {view === "overview" && (
          <DashboardPage
            onNavigate={navigate}
            preferredOrganizationId={organizationId}
            onOrganizationChange={setOrganizationId}
          />
        )}
        {(view === "missions" || view === "deployments") && (
          <RecordsPage
            key={view}
            kind={view}
            preferredOrganizationId={organizationId}
            onOrganizationChange={setOrganizationId}
          />
        )}
        {view === "logbook" && (
          <LogbookPage
            preferredOrganizationId={organizationId}
            onOrganizationChange={setOrganizationId}
          />
        )}
        {view === "identity" && <IdentityPage />}
        {view === "robots" && (
          <FleetPage
            robotId={robotId}
            onSelectRobot={setRobotId}
            preferredOrganizationId={organizationId}
            onOrganizationChange={setOrganizationId}
          />
        )}
        {view === "health" && <HealthPage />}
      </div>
    </div>
  );
}
