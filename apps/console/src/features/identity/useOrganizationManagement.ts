import { useCallback, useEffect, useState } from "react";
import { useApiToken } from "@/hooks/useApiToken";
import { apiFetch, apiFetchAll } from "@/lib/api";
export type Role = "owner" | "developer" | "operator";
type User = {
  id: string;
  created_at: string;
  name: string | null;
  email: string | null;
  profile_picture_url: string | null;
  phone_number: string | null;
};
type Membership = {
  organization_id: string;
  user_id: string;
  role: Role;
  created_at: string;
  name: string | null;
  email: string | null;
  profile_picture_url: string | null;
};
type MeResponse = { user: User; memberships: Membership[] };
type OrganizationItem = {
  id: string;
  name: string;
  role: Role;
  created_at: string;
};
type Invitation = {
  id: string;
  organization_id: string;
  email: string | null;
  role: Role;
  created_at: string;
  expires_at: string;
  accepted_at: string | null;
  accepted_by_user_id: string | null;
  revoked_at: string | null;
};

export const ROLE_OPTIONS: Role[] = ["operator", "developer", "owner"];

/** Organization/member/invitation operations are independent of their table layout. */
export function useOrganizationManagement(
  onOrganizationLeft?: (
    organizationId: string,
    remaining: OrganizationItem[],
  ) => void,
  onOrganizationJoined?: (organization: OrganizationItem) => void,
) {
  const getToken = useApiToken();

  const [me, setMe] = useState<MeResponse | null>(null);
  const [organizations, setOrganizations] = useState<OrganizationItem[] | null>(
    null,
  );
  const [selectedOrg, setSelectedOrg] = useState<string | null>(null);
  const [members, setMembers] = useState<Membership[] | null>(null);
  const [invitations, setInvitations] = useState<Invitation[] | null>(null);
  const [newOrgName, setNewOrgName] = useState("");
  const [inviteEmail, setInviteEmail] = useState("");
  const [inviteRole, setInviteRole] = useState<Role>("operator");
  const [invitationCode, setInvitationCode] = useState<string | null>(null);
  const [joinCode, setJoinCode] = useState("");
  const [joinPreview, setJoinPreview] = useState<{
    organization_id: string;
    organization_name: string;
    role: Role;
    token: string;
  } | null>(null);
  // biome-ignore lint/correctness/useExhaustiveDependencies: Discard the one-time code when the selected organization changes.
  useEffect(() => {
    setInvitationCode(null);
  }, [selectedOrg]);
  const [busy, setBusy] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const loadIdentity = useCallback(async () => {
    try {
      const [meResponse, orgs] = await Promise.all([
        apiFetch(getToken, "/v1/me").then(
          (r) => r.json() as Promise<MeResponse>,
        ),
        apiFetchAll<OrganizationItem>(getToken, "/v1/organizations"),
      ]);
      setMe(meResponse);
      setOrganizations(orgs);
      setError(null);
      setSelectedOrg((current) =>
        current && orgs.some((org) => org.id === current)
          ? current
          : (orgs[0]?.id ?? null),
      );
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Request failed");
    }
  }, [getToken]);

  useEffect(() => {
    void loadIdentity();
  }, [loadIdentity]);

  const loadOrganization = useCallback(async () => {
    if (!selectedOrg) {
      setMembers(null);
      setInvitations(null);
      return;
    }
    setMembers(null);
    setInvitations(null);
    try {
      const memberItems = await apiFetchAll<Membership>(
        getToken,
        `/v1/organizations/${selectedOrg}/members`,
      );
      setMembers(memberItems);
      if (
        organizations?.find((org) => org.id === selectedOrg)?.role === "owner"
      ) {
        const invitationResponse = (await apiFetch(
          getToken,
          `/v1/organizations/${selectedOrg}/invitations`,
        ).then((response) => response.json())) as { items: Invitation[] };
        setInvitations(invitationResponse.items);
      } else {
        setInvitations([]);
      }
      setError(null);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Request failed");
    }
  }, [getToken, organizations, selectedOrg]);

  useEffect(() => {
    void loadOrganization();
  }, [loadOrganization]);

  const createOrganization = async (event: React.FormEvent) => {
    event.preventDefault();
    setBusy("create-organization");
    setNotice(null);
    try {
      const created = (await apiFetch(getToken, "/v1/organizations", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name: newOrgName }),
      }).then((response) => response.json())) as OrganizationItem;
      setNewOrgName("");
      await loadIdentity();
      setSelectedOrg(created.id);
      setNotice(`Created ${created.name}.`);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Request failed");
    } finally {
      setBusy(null);
    }
  };

  const createInvitationCode = async () => {
    if (!selectedOrg || busy !== null) return;
    setBusy("invite-code");
    setError(null);
    setNotice(null);
    setInvitationCode(null);
    try {
      const created = (await apiFetch(
        getToken,
        `/v1/organizations/${selectedOrg}/invitation-codes`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ role: inviteRole }),
        },
      ).then((r) => r.json())) as { code: string };
      setInvitationCode(created.code);
      setNotice(
        "Invitation code created. Share it with the person you want to invite.",
      );
      await loadOrganization();
    } catch (cause) {
      setError(
        cause instanceof Error
          ? cause.message
          : "Could not create invitation code",
      );
    } finally {
      setBusy(null);
    }
  };

  const previewInvitationCode = async (event: React.FormEvent) => {
    event.preventDefault();
    if (busy !== null) return;
    setBusy("preview-code");
    setError(null);
    setNotice(null);
    setJoinPreview(null);
    try {
      const token = joinCode.trim();
      const preview = (await apiFetch(getToken, "/v1/invitations/preview", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ token }),
      }).then((r) => r.json())) as {
        organization_id: string;
        organization_name: string;
        role: Role;
      };
      setJoinPreview({ ...preview, token });
    } catch (cause) {
      setError(
        cause instanceof Error ? cause.message : "Invalid invitation code",
      );
    } finally {
      setBusy(null);
    }
  };

  const acceptInvitationCode = async () => {
    if (!joinPreview || busy !== null) return;
    setBusy("join-code");
    setError(null);
    setNotice(null);
    try {
      const membership = (await apiFetch(getToken, "/v1/invitations/accept", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ token: joinPreview.token }),
      }).then((r) => r.json())) as Membership;
      await loadIdentity();
      setSelectedOrg(membership.organization_id);
      onOrganizationJoined?.({
        id: membership.organization_id,
        name: joinPreview.organization_name,
        role: membership.role,
        created_at: membership.created_at,
      });
      setNotice(
        `Joined ${joinPreview.organization_name} as ${membership.role}.`,
      );
      setJoinCode("");
      setJoinPreview(null);
    } catch (cause) {
      setError(
        cause instanceof Error ? cause.message : "Could not join organization",
      );
    } finally {
      setBusy(null);
    }
  };

  const inviteMember = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!selectedOrg) return;
    setBusy("invite");
    setNotice(null);
    setError(null);
    try {
      await apiFetch(getToken, `/v1/organizations/${selectedOrg}/invitations`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ email: inviteEmail, role: inviteRole }),
      });
      setInviteEmail("");
      setNotice("Invitation email sent.");
      await loadOrganization();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Request failed");
    } finally {
      setBusy(null);
    }
  };

  const changeRole = async (member: Membership, role: Role) => {
    if (!selectedOrg || role === member.role) return;
    setBusy(`role-${member.user_id}`);
    setError(null);
    setNotice(null);
    try {
      await apiFetch(
        getToken,
        `/v1/organizations/${selectedOrg}/members/${member.user_id}`,
        {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ role }),
        },
      );
      setNotice("Member role updated.");
      await loadOrganization();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Request failed");
    } finally {
      setBusy(null);
    }
  };

  const removeMember = async (member: Membership) => {
    if (!selectedOrg) return;
    if (!window.confirm(`Remove ${member.user_id} from this organization?`))
      return;
    setBusy(`remove-${member.user_id}`);
    setError(null);
    setNotice(null);
    try {
      await apiFetch(
        getToken,
        `/v1/organizations/${selectedOrg}/members/${member.user_id}`,
        { method: "DELETE" },
      );
      setNotice("Member removed.");
      await loadOrganization();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Request failed");
    } finally {
      setBusy(null);
    }
  };

  const removeOrganizationFromView = async (
    organization: OrganizationItem,
    deleted: boolean,
  ) => {
    const remaining = (organizations ?? []).filter(
      (org) => org.id !== organization.id,
    );
    setOrganizations(remaining);
    setMe((current) =>
      current
        ? {
            ...current,
            memberships: current.memberships.filter(
              (m) => m.organization_id !== organization.id,
            ),
          }
        : current,
    );
    setMembers(null);
    setInvitations(null);
    setSelectedOrg(remaining[0]?.id ?? null);
    onOrganizationLeft?.(organization.id, remaining);
    setNotice(
      deleted
        ? `Deleted ${organization.name}.`
        : `You left ${organization.name}.`,
    );
    await loadIdentity();
  };

  const deleteOrganization = async (leavingLastMember = false) => {
    const organization = organizations?.find((org) => org.id === selectedOrg);
    if (organization?.role !== "owner" || busy !== null) return;
    const prompt = leavingLastMember
      ? `You are the last member of ${organization.name}. Leaving will delete this organization. Continue?`
      : `Delete ${organization.name} for all members? Everyone will lose access and its robot credentials will be revoked. This cannot be undone.`;
    if (!window.confirm(prompt)) return;
    setBusy("delete-organization");
    setError(null);
    setNotice(null);
    try {
      await apiFetch(getToken, `/v1/organizations/${organization.id}`, {
        method: "DELETE",
      });
      await removeOrganizationFromView(organization, true);
    } catch (cause) {
      setError(
        cause instanceof Error
          ? cause.message
          : "Could not delete the organization",
      );
    } finally {
      setBusy(null);
    }
  };

  const leaveOrganization = async () => {
    const organization = organizations?.find((org) => org.id === selectedOrg);
    if (!organization || busy !== null) return;
    if (organization.role === "owner" && members?.length === 1) {
      await deleteOrganization(true);
      return;
    }
    if (
      !window.confirm(
        `Leave ${organization.name}? You will lose access to its robots and Apps. An owner must invite you again to rejoin.`,
      )
    )
      return;
    setBusy("leave-organization");
    setError(null);
    setNotice(null);
    try {
      await apiFetch(
        getToken,
        `/v1/organizations/${organization.id}/members/me`,
        { method: "DELETE" },
      );
      await removeOrganizationFromView(organization, false);
    } catch (cause) {
      setError(
        cause instanceof Error
          ? cause.message
          : "Could not leave the organization",
      );
    } finally {
      setBusy(null);
    }
  };

  const resendInvitation = async (invitation: Invitation) => {
    if (!selectedOrg) return;
    setBusy(`resend-${invitation.id}`);
    setError(null);
    setNotice(null);
    try {
      await apiFetch(
        getToken,
        `/v1/organizations/${selectedOrg}/invitations/${invitation.id}/resend`,
        { method: "POST" },
      );
      setNotice(`Invitation resent to ${invitation.email}.`);
      await loadOrganization();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Request failed");
    } finally {
      setBusy(null);
    }
  };

  const revokeInvitation = async (invitation: Invitation) => {
    if (!selectedOrg) return;
    setBusy(`revoke-${invitation.id}`);
    setError(null);
    setNotice(null);
    try {
      await apiFetch(
        getToken,
        `/v1/organizations/${selectedOrg}/invitations/${invitation.id}`,
        { method: "DELETE" },
      );
      setNotice(
        invitation.email
          ? `Invitation to ${invitation.email} revoked.`
          : "Invitation code revoked.",
      );
      await loadOrganization();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Request failed");
    } finally {
      setBusy(null);
    }
  };

  const selected = organizations?.find((org) => org.id === selectedOrg);
  const canManage = selected?.role === "owner";
  const isLastOwner =
    canManage &&
    members !== null &&
    members.filter((member) => member.role === "owner").length <= 1;

  return {
    me,
    organizations,
    selectedOrg,
    members,
    invitations,
    newOrgName,
    inviteEmail,
    inviteRole,
    busy,
    notice,
    error,
    selected,
    canManage,
    createOrganization,
    inviteMember,
    invitationCode,
    joinCode,
    joinPreview,
    setJoinCode: (value: string) => {
      setJoinCode(value);
      setJoinPreview(null);
    },
    createInvitationCode,
    previewInvitationCode,
    acceptInvitationCode,
    changeRole,
    removeMember,
    leaveOrganization,
    isLastOwner,
    isOnlyMember: members?.length === 1,
    deleteOrganization,
    resendInvitation,
    revokeInvitation,
    setSelectedOrg,
    setNewOrgName,
    setInviteEmail,
    setInviteRole,
  };
}
