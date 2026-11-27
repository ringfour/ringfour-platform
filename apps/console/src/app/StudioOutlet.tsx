import { HealthPage } from "@/components/HealthPage";
import { PageHeading } from "@/components/PageHeading";
import { AppStorePage } from "@/features/apps/AppStorePage";
import { AppsPage } from "@/features/apps/AppsPage";
import { IdentityPage } from "@/features/identity/IdentityPage";
import { ProfilePage } from "@/features/identity/ProfilePage";
import { AppLogbookPage } from "@/features/logbook/AppLogbookPage";
import { AppTimelinePage } from "@/features/logbook/AppTimelinePage";
import { HomePage } from "@/features/overview/HomePage";
import { RobotsWorkspacePage } from "@/features/robots/RobotsWorkspacePage";
import { type StudioRoute, type StudioView, studioPath } from "@/lib/location";
import { StudioPage } from "@/views/StudioPage";

/** The single route-to-feature boundary. Feature pages own their own view state. */
export function StudioOutlet({
  route,
  workspace,
  role,
  workspaces,
  onWorkspaceChange,
  navigate,
  onOrganizationLeft,
  onOrganizationJoined,
}: {
  route: StudioRoute;
  workspace: string;
  role?: string;
  workspaces: { id: string; name: string; role?: string }[];
  onWorkspaceChange: (id: string) => void;
  onOrganizationJoined?: (organization: {
    id: string;
    name: string;
    role?: string;
  }) => void;
  onOrganizationLeft?: (
    id: string,
    remaining: { id: string; name: string; role?: string }[],
  ) => void;
  navigate: (next: StudioView, id?: string, organization?: string) => void;
}) {
  const { view, id, section } = route;
  if (view === "unavailable")
    return (
      <StudioPage heading={<PageHeading title="Page unavailable" />}>
        <p>
          This link is no longer available. Choose a destination from Studio.
        </p>
        <a href={studioPath("overview", workspace)}>Return Home →</a>
      </StudioPage>
    );
  if (!workspace && !["identity", "health", "profile"].includes(view))
    return (
      <StudioPage
        heading={
          <PageHeading
            title="Choose a workspace"
            description="Create or join a workspace to view its robots and Apps."
            actions={
              <button
                className="button-primary"
                type="button"
                onClick={() => navigate("identity")}
              >
                Open workspace settings
              </button>
            }
          />
        }
      />
    );
  switch (view) {
    case "overview":
      return (
        <HomePage
          key={workspace}
          organizationId={workspace}
          workspaceRole={role}
        />
      );
    case "apps":
      return (
        <AppsPage
          key={workspace}
          organizationId={workspace}
          workspaceRole={role}
          focusAppId={id || undefined}
          focusSection={section || "overview"}
        />
      );
    case "store":
      return (
        <AppStorePage
          workspaceName={workspaces.find((w) => w.id === workspace)?.name}
          key={workspace}
          organizationId={workspace}
          workspaceRole={role}
        />
      );
    case "robots":
      return (
        <RobotsWorkspacePage
          key={workspace}
          organizationId={workspace}
          workspaceRole={role}
          robotId={id || undefined}
          section={section}
          onSelectRobot={(value, org) => navigate("robots", value, org)}
        />
      );
    case "logbook":
      return (
        <AppLogbookPage
          key={workspace + id}
          organizationId={workspace}
          runId={id || undefined}
          workspaceRole={role}
        />
      );
    case "timeline":
      return <AppTimelinePage key={workspace} organizationId={workspace} />;
    case "identity":
      return (
        <IdentityPage
          onOrganizationLeft={onOrganizationLeft}
          onOrganizationJoined={onOrganizationJoined}
        />
      );
    case "profile":
      return (
        <ProfilePage
          workspace={workspace}
          workspaces={workspaces}
          onWorkspaceChange={onWorkspaceChange}
        />
      );
    case "health":
      return <HealthPage />;
  }
}
