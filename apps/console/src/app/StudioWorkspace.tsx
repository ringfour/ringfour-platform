import { InvitationAcceptancePage } from "@/features/identity/InvitationAcceptancePage";
import { StudioShell } from "@/views/StudioShell";
import { StudioOutlet } from "./StudioOutlet";
import { useStudioNavigation } from "./useStudioNavigation";
import { useWorkspace } from "./useWorkspace";

export function StudioWorkspace() {
  const route = useStudioNavigation();
  const {
    workspaces,
    workspace,
    role,
    navigate,
    changeWorkspace,
    organizationLeft,
    organizationJoined,
  } = useWorkspace(route);
  const invitationToken = new URLSearchParams(
    window.location.hash.slice(1),
  ).get("invite");
  if (invitationToken)
    return <InvitationAcceptancePage token={invitationToken} />;
  return (
    <StudioShell
      workspaces={workspaces}
      workspace={workspace}
      view={route.view}
      onWorkspaceChange={changeWorkspace}
    >
      <StudioOutlet
        route={route}
        workspace={workspace}
        role={role}
        workspaces={workspaces}
        onWorkspaceChange={changeWorkspace}
        navigate={navigate}
        onOrganizationLeft={organizationLeft}
        onOrganizationJoined={organizationJoined}
      />
    </StudioShell>
  );
}
