import { useAuth0 } from "@auth0/auth0-react";
import { useCallback, useEffect, useState } from "react";

import { PageHeading } from "@/components/PageHeading";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { StatusBadge } from "@/components/ui/status-badge";
import { apiFetch, apiFetchAll } from "@/lib/api";

type Role = "owner" | "developer" | "operator";
type User = { id: string; created_at: string; name: string | null; email: string | null; profile_picture_url: string | null; phone_number: string | null };
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
  email: string;
  role: Role;
  created_at: string;
  expires_at: string;
  accepted_at: string | null;
  accepted_by_user_id: string | null;
  revoked_at: string | null;
};

const ROLE_OPTIONS: Role[] = ["operator", "developer", "owner"];

export function IdentityPage() {
  const { getAccessTokenSilently, logout } = useAuth0();
  const getToken = useCallback(async (): Promise<string> => {
    const token = await getAccessTokenSilently({
      authorizationParams: { audience: import.meta.env.VITE_AUTH0_AUDIENCE },
    });
    if (!token) throw new Error("No access token available");
    return token;
  }, [getAccessTokenSilently]);

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
  const [busy, setBusy] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const loadIdentity = useCallback(async () => {
    try {
      const [meResponse, orgs] = await Promise.all([
        apiFetch(getToken, "/v1/me").then((r) => r.json() as Promise<MeResponse>),
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
      if (organizations?.find((org) => org.id === selectedOrg)?.role === "owner") {
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
    if (!window.confirm(`Remove ${member.user_id} from this organization?`)) return;
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
      setNotice(`Invitation to ${invitation.email} revoked.`);
      await loadOrganization();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Request failed");
    } finally {
      setBusy(null);
    }
  };

  const selected = organizations?.find((org) => org.id === selectedOrg);
  const canManage = selected?.role === "owner";

  return (
    <main className="workspace-page space-y-6">
      <PageHeading
        title="Organizations"
        description="Create workspaces, manage member roles, and invite people by email."
      />

      <Card>
        <CardHeader>
          <CardTitle>Your organizations</CardTitle>
        </CardHeader>
        <CardContent>
          {organizations === null ? (
            <p className="text-muted-foreground">Loading organizations…</p>
          ) : organizations.length === 0 ? (
            <p className="text-muted-foreground">You do not belong to an organization yet.</p>
          ) : (
            <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
              {organizations.map((org) => (
                <button
                  key={org.id}
                  type="button"
                  aria-pressed={selectedOrg === org.id}
                  onClick={() => setSelectedOrg(org.id)}
                  className={`rounded-lg border p-4 text-left transition-colors ${selectedOrg === org.id ? "border-primary bg-primary/5" : "hover:bg-muted/50"}`}
                >
                  <span className="flex items-center justify-between gap-3">
                    <span className="font-semibold">{org.name}</span>
                    <StatusBadge
                      variant={org.role === "owner" ? "success" : "neutral"}
                      label={org.role}
                    />
                  </span>
                  <span className="mt-2 block break-all text-xs text-muted-foreground">
                    {org.id}
                  </span>
                </button>
              ))}
            </div>
          )}
          {organizations !== null && (
            <form onSubmit={createOrganization} className="mt-5 flex gap-2">
              <input
                required
                maxLength={200}
                className="min-w-0 flex-1 rounded border bg-transparent px-3 py-2 text-sm"
                placeholder="New organization name"
                value={newOrgName}
                onChange={(event) => setNewOrgName(event.target.value)}
              />
              <button
                type="submit"
                disabled={busy !== null}
                className="rounded-md bg-primary px-4 py-2 text-sm font-medium text-primary-foreground disabled:opacity-50"
              >
                Create organization
              </button>
            </form>
          )}
        </CardContent>
      </Card>

      {selected && (
        <>
          <Card>
            <CardHeader>
              <CardTitle>{selected.name} · Members</CardTitle>
            </CardHeader>
            <CardContent>
              {members === null ? (
                <p className="text-muted-foreground">Loading members…</p>
              ) : members.length === 0 ? (
                <p className="text-muted-foreground">No members found.</p>
              ) : (
                <div className="overflow-x-auto">
                  <table className="w-full text-sm">
                    <thead>
                      <tr className="border-b text-left text-muted-foreground">
                        <th className="py-2 pr-4 font-medium">Member</th>
                        <th className="py-2 pr-4 font-medium">Role</th>
                        {canManage && <th className="py-2 font-medium">Actions</th>}
                      </tr>
                    </thead>
                    <tbody>
                      {members.map((member) => (
                        <tr key={member.user_id} className="border-b last:border-0">
                          <td className="break-all py-3 pr-4">
                            <div className="flex items-center gap-3">
                              {member.profile_picture_url ? (
                                <img src={member.profile_picture_url} alt="" referrerPolicy="no-referrer" className="h-9 w-9 shrink-0 rounded-full border object-cover" />
                              ) : (
                                <span aria-hidden="true" className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full border bg-muted text-xs">{member.name?.trim().charAt(0).toUpperCase() || "?"}</span>
                              )}
                              <span className="min-w-0">
                                <span className="block font-medium">{member.name || "Unnamed user"}</span>
                                <span className="block text-xs text-muted-foreground">{member.email || "Email unavailable"}</span>
                                <span className="mt-1 block text-xs text-muted-foreground">{member.user_id}</span>
                              </span>
                            </div>
                          </td>
                          <td className="py-3 pr-4">
                            {canManage ? (
                              <select
                                aria-label={`Role for ${member.user_id}`}
                                className="rounded border bg-background px-2 py-1"
                                value={member.role}
                                disabled={busy !== null}
                                onChange={(event) =>
                                  void changeRole(member, event.target.value as Role)
                                }
                              >
                                {ROLE_OPTIONS.map((role) => (
                                  <option key={role} value={role}>{role}</option>
                                ))}
                              </select>
                            ) : (
                              <StatusBadge
                                variant={member.role === "owner" ? "success" : "neutral"}
                                label={member.role}
                              />
                            )}
                          </td>
                          {canManage && (
                            <td className="py-3">
                              <button
                                type="button"
                                disabled={busy !== null}
                                className="text-destructive underline-offset-2 hover:underline disabled:opacity-50"
                                onClick={() => void removeMember(member)}
                              >
                                Remove
                              </button>
                            </td>
                          )}
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Invitations</CardTitle>
            </CardHeader>
            <CardContent className="space-y-5">
              {canManage ? (
                <form onSubmit={inviteMember} className="grid gap-3 sm:grid-cols-[1fr_10rem_auto]">
                  <input
                    required
                    type="email"
                    maxLength={320}
                    className="rounded border bg-transparent px-3 py-2 text-sm"
                    placeholder="name@example.com"
                    value={inviteEmail}
                    onChange={(event) => setInviteEmail(event.target.value)}
                  />
                  <select
                    aria-label="Invitation role"
                    className="rounded border bg-background px-3 py-2 text-sm"
                    value={inviteRole}
                    onChange={(event) => setInviteRole(event.target.value as Role)}
                  >
                    {ROLE_OPTIONS.map((role) => (
                      <option key={role} value={role}>{role}</option>
                    ))}
                  </select>
                  <button
                    type="submit"
                    disabled={busy !== null}
                    className="rounded-md bg-primary px-4 py-2 text-sm font-medium text-primary-foreground disabled:opacity-50"
                  >
                    {busy === "invite" ? "Sending…" : "Send invitation"}
                  </button>
                </form>
              ) : (
                <p className="text-sm text-muted-foreground">Only organization owners can send or manage invitations.</p>
              )}

              {invitations === null ? (
                <p className="text-muted-foreground">Loading invitations…</p>
              ) : invitations.length === 0 ? (
                <p className="text-muted-foreground">No invitations yet.</p>
              ) : (
                <div className="overflow-x-auto">
                  <table className="w-full text-sm">
                    <thead>
                      <tr className="border-b text-left text-muted-foreground">
                        <th className="py-2 pr-4 font-medium">Email</th>
                        <th className="py-2 pr-4 font-medium">Role</th>
                        <th className="py-2 pr-4 font-medium">Status</th>
                        <th className="py-2 pr-4 font-medium">Expires</th>
                        {canManage && <th className="py-2 font-medium">Actions</th>}
                      </tr>
                    </thead>
                    <tbody>
                      {invitations.map((invitation) => {
                        const state = invitation.accepted_at
                          ? "accepted"
                          : invitation.revoked_at
                            ? "revoked"
                            : Date.parse(invitation.expires_at) <= Date.now()
                              ? "expired"
                              : "pending";
                        return (
                          <tr key={invitation.id} className="border-b last:border-0">
                            <td className="py-3 pr-4">{invitation.email}</td>
                            <td className="py-3 pr-4">{invitation.role}</td>
                            <td className="py-3 pr-4">
                              <StatusBadge variant={state === "accepted" ? "success" : "neutral"} label={state} />
                            </td>
                            <td className="py-3 pr-4">{new Date(invitation.expires_at).toLocaleString()}</td>
                            {canManage && (
                              <td className="py-3">
                                {state === "pending" || state === "expired" ? (
                                  <span className="flex gap-3">
                                    <button
                                      type="button"
                                      disabled={busy !== null}
                                      className="text-primary underline-offset-2 hover:underline disabled:opacity-50"
                                      onClick={() => void resendInvitation(invitation)}
                                    >
                                      Resend
                                    </button>
                                    <button
                                      type="button"
                                      disabled={busy !== null}
                                      className="text-destructive underline-offset-2 hover:underline disabled:opacity-50"
                                      onClick={() => void revokeInvitation(invitation)}
                                    >
                                      Revoke
                                    </button>
                                  </span>
                                ) : "—"}
                              </td>
                            )}
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              )}
            </CardContent>
          </Card>
        </>
      )}

      <Card>
        <CardHeader><CardTitle>Account</CardTitle></CardHeader>
        <CardContent>
          {me && (
            <p className="text-sm">Signed in as <span className="font-medium">{me.user.id}</span></p>
          )}
          <button
            type="button"
            className="mt-4 rounded-md border px-3 py-1.5 text-sm"
            onClick={() => logout({ logoutParams: { returnTo: window.location.origin } })}
          >
            Sign out
          </button>
        </CardContent>
      </Card>

      {notice && <p role="status" className="text-sm text-green-700">{notice}</p>}
      {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
    </main>
  );
}
