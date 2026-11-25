import { useAuth0 } from "@auth0/auth0-react";
import { useEffect, useRef, useState } from "react";
import { AppLogbookPage } from "@/components/AppLogbookPage";
import { AppsPage, StoreRoute } from "@/components/AppsPage";
import { AppTimelinePage } from "@/components/AppTimelinePage";
import { HealthPage } from "@/components/HealthPage";
import { HomePage } from "@/components/HomePage";
import { IdentityPage } from "@/components/IdentityPage";
import { InvitationAcceptancePage } from "@/components/InvitationAcceptancePage";
import { LoginPage } from "@/components/LoginPage";
import { ProfilePage } from "@/components/ProfilePage";
import { RobotsWorkspacePage } from "@/components/RobotsWorkspacePage";
import { AUTH_SESSION_EXPIRED_EVENT, apiFetchAll } from "@/lib/api";

import { resourcePath, useLocation, visit } from "@/lib/location";

const routes = {
  overview: "overview",
  robots: "robots",
  logbook: "runs",
  timeline: "timeline",
  apps: "apps",
  store: "app-store",
  identity: "settings/workspace",
  health: "settings/health",
  profile: "settings/profile",
};
type View =
  | "overview"
  | "robots"
  | "apps"
  | "store"
  | "identity"
  | "health"
  | "profile"
  | "logbook"
  | "timeline";

const NAV: { id: View; label: string; icon: string }[] = [
  { id: "overview", label: "Home", icon: "overview" },
  { id: "robots", label: "Robots", icon: "fleet" },
  { id: "apps", label: "Apps", icon: "apps" },
  { id: "store", label: "App Store", icon: "apps" },
  { id: "logbook", label: "Logbook", icon: "logbook" },
  { id: "identity", label: "Organizations", icon: "workspace" },
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
    apps: (
      <>
        <path d="M5 3h10l4 4v14H5z" />
        <path d="M15 3v5h4M8 12h8M8 16h8" />
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
    profile: (
      <>
        <circle cx="12" cy="8" r="3.5" />
        <path d="M5 20v-1a7 7 0 0 1 14 0v1" />
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
  const {
    getAccessTokenSilently,
    isAuthenticated,
    isLoading,
    error,
    loginWithRedirect,
  } = useAuth0();
  const [authWaitExpired, setAuthWaitExpired] = useState(false);
  const [authRedirecting, setAuthRedirecting] = useState(false);
  const [authRedirectError, setAuthRedirectError] = useState(false);
  const authRedirectStarted = useRef(false);
  const location = useLocation();
  const path = location.pathname.slice(1);
  const view =
    (Object.keys(routes) as View[]).find(
      (key) => path === routes[key] || path.startsWith(routes[key] + "/"),
    ) ?? "overview";
  const id = path.slice(routes[view].length + 1);
  const [menuOpen, setMenuOpen] = useState(false);
  const [workspace, setOrganizationId] = useState("");
  const organizationId = location.searchParams.get("workspace") ?? workspace;
  const [workspaces, setWorkspaces] = useState<
    { id: string; name: string; role?: string }[]
  >([]);
  const activeOrganizationId = organizationId || workspaces[0]?.id || "";
  const activeRole = workspaces.find(
    (item) => item.id === activeOrganizationId,
  )?.role;
  useEffect(() => {
    if (!isAuthenticated) return;
    let current = true;
    void apiFetchAll<{ id: string; name: string; role?: string }>(async () => {
      const token = await getAccessTokenSilently({
        authorizationParams: { audience: import.meta.env.VITE_AUTH0_AUDIENCE },
      });
      if (!token) throw new Error("Sign in required");
      return token;
    }, "/v1/organizations")
      .then((items) => {
        if (current) setWorkspaces(items);
      })
      .catch(() => {});
    return () => {
      current = false;
    };
  }, [isAuthenticated, getAccessTokenSilently]);
  const navigate = (next: View, id = "", org = organizationId) => {
    setMenuOpen(false);
    const list = window.history.state?.fromList;
    if (
      !id &&
      typeof list === "string" &&
      window.location.pathname.startsWith("/" + routes[next] + "/") &&
      new URL(list, window.location.origin).pathname === "/" + routes[next] &&
      org === organizationId
    ) {
      const top = window.history.state?.listScroll;
      visit(list);
      if (typeof top === "number")
        requestAnimationFrame(() => window.scrollTo(0, top));
    } else visit(resourcePath(routes[next], id, org));
  };

  useEffect(() => {
    if (!isLoading) {
      setAuthWaitExpired(false);
      return;
    }
    const timeout = window.setTimeout(() => setAuthWaitExpired(true), 10_000);
    return () => window.clearTimeout(timeout);
  }, [isLoading]);

  useEffect(() => {
    const handleExpiredSession = () => {
      if (!isAuthenticated || authRedirectStarted.current) return;
      authRedirectStarted.current = true;
      setAuthRedirecting(true);
      const returnTo = [
        window.location.pathname,
        window.location.search,
        window.location.hash,
      ].join("");
      void Promise.resolve(loginWithRedirect({ appState: { returnTo } })).catch(
        () => {
          authRedirectStarted.current = false;
          setAuthRedirecting(false);
          setAuthRedirectError(true);
        },
      );
    };
    window.addEventListener(AUTH_SESSION_EXPIRED_EVENT, handleExpiredSession);
    return () => {
      window.removeEventListener(
        AUTH_SESSION_EXPIRED_EVENT,
        handleExpiredSession,
      );
    };
  }, [isAuthenticated, loginWithRedirect]);

  if (authRedirecting)
    return (
      <main className="auth-wait">
        <p role="status">Your session expired. Redirecting to sign in…</p>
      </main>
    );
  if (authRedirectError)
    return (
      <main className="auth-wait">
        <p role="alert" className="text-danger">
          Your session expired. Sign in again to continue.
        </p>
        <button
          type="button"
          className="button-primary"
          onClick={() => void loginWithRedirect()}
        >
          Sign in
        </button>
      </main>
    );

  if (isLoading && authWaitExpired)
    return (
      <main className="auth-wait">
        <p role="alert" className="text-danger">
          Authentication is taking too long. Open Console at{" "}
          <a
            href={import.meta.env.VITE_CONSOLE_ORIGIN || window.location.origin}
          >
            {import.meta.env.VITE_CONSOLE_ORIGIN || window.location.origin}
          </a>
          .
        </p>
      </main>
    );
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

  const invitationToken = new URLSearchParams(
    window.location.hash.slice(1),
  ).get("invite");
  if (invitationToken)
    return <InvitationAcceptancePage token={invitationToken} />;

  return (
    <div className="console-shell">
      <aside className="console-sidebar" data-menu-open={menuOpen}>
        <div className="console-brand">
          <img src="/ringfour-logo.svg" alt="" />
          <div className="console-brand-copy">
            <strong>ringfour</strong>
            <small>Console</small>
          </div>
          <button
            type="button"
            className="console-menu-toggle button-secondary"
            aria-expanded={menuOpen}
            aria-controls="console-main-nav console-settings"
            onClick={() => setMenuOpen((open) => !open)}
          >
            {menuOpen ? "Close menu" : "Menu"}
          </button>
        </div>
        <nav
          id="console-main-nav"
          className="console-nav"
          aria-label="Main navigation"
        >
          {NAV.filter(
            (item) => item.id !== "identity" && item.id !== "health",
          ).map((item) => (
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
        <div id="console-settings" className="console-settings">
          {workspaces.length > 0 && (
            <label className="resource-workspace">
              Workspace
              <select
                value={organizationId || workspaces[0].id}
                onChange={(event) => {
                  setOrganizationId(event.target.value);
                  navigate("overview", "", event.target.value);
                }}
              >
                {workspaces.map((org) => (
                  <option key={org.id} value={org.id}>
                    {org.name}
                  </option>
                ))}
              </select>
            </label>
          )}
          <div className="sidebar-label">Settings</div>
          <nav className="console-nav" aria-label="Settings">
            {NAV.filter(
              (item) => item.id === "identity" || item.id === "health",
            ).map((item) => (
              <button
                key={item.id}
                type="button"
                aria-current={view === item.id ? "page" : undefined}
                onClick={() => navigate(item.id)}
              >
                <Icon name={item.icon} />
                <span>{item.label}</span>
              </button>
            ))}
            <button
              type="button"
              aria-current={view === "profile" ? "page" : undefined}
              onClick={() => navigate("profile")}
            >
              <Icon name="profile" />
              <span>My profile</span>
              {view === "profile" && (
                <span className="nav-active-dot" aria-hidden="true" />
              )}
            </button>
          </nav>
        </div>
      </aside>
      <div className="console-body">
        <header className="console-topbar">
          <div>
            Console{" "}
            <span className="topbar-separator" aria-hidden="true">
              /
            </span>{" "}
            <strong>
              {NAV.find((item) => item.id === view)?.label ?? "My profile"}
            </strong>
          </div>
          <div className="topbar-right">
            <a href="/studio/index.html">Studio prototype ↗</a>
            <span className="console-environment">Development</span>
          </div>
        </header>
        {view === "timeline" && (
          <div
            id="console-timeline-toolbar"
            className="console-secondary-toolbar"
            role="toolbar"
            aria-label="Timeline controls"
          />
        )}
        {!activeOrganizationId &&
          !["identity", "health", "profile"].includes(view) && (
            <main className="app-workflow-page">
              <section className="app-panel">
                <h1>Choose a workspace</h1>
                <p>Create or join a workspace to view its robots and Apps.</p>
                <button
                  className="button-primary"
                  type="button"
                  onClick={() => navigate("identity")}
                >
                  Open workspace settings
                </button>
              </section>
            </main>
          )}
        {activeOrganizationId && view === "overview" && (
          <HomePage
            key={activeOrganizationId}
            organizationId={activeOrganizationId}
            workspaceRole={activeRole}
          />
        )}
        {activeOrganizationId &&
          view === "apps" && (
            <AppsPage
              key={activeOrganizationId}
              organizationId={activeOrganizationId}
              workspaceRole={activeRole}
              focusAppId={id.split("/")[0] || undefined}
              focusSection={id.split("/")[1] || "overview"}
            />
          )}
        {activeOrganizationId && view === "store" && (
          <StoreRoute
            key={activeOrganizationId}
            organizationId={activeOrganizationId}
            workspaceRole={activeRole}
          />
        )}
        {activeOrganizationId && view === "logbook" && (
          <AppLogbookPage
            key={activeOrganizationId + id}
            organizationId={activeOrganizationId}
            runId={id || undefined}
            selectRun={(value) => navigate("logbook", value)}
          />
        )}
        {activeOrganizationId && view === "timeline" && (
          <AppTimelinePage
            key={activeOrganizationId}
            organizationId={activeOrganizationId}
          />
        )}
        {view === "identity" && <IdentityPage />}
        {view === "profile" && <ProfilePage />}
        {activeOrganizationId && view === "robots" && (
          <RobotsWorkspacePage
            key={activeOrganizationId}
            robotId={id}
            onSelectRobot={(value, org) => navigate("robots", value, org)}
            organizationId={activeOrganizationId}
            workspaceRole={activeRole}
          />
        )}
        {view === "health" && <HealthPage />}
      </div>
    </div>
  );
}
