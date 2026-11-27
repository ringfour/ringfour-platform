import { useAuth0 } from "@auth0/auth0-react";
import { PageHeading } from "@/components/PageHeading";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { StatusBadge } from "@/components/ui/status-badge";
import {
  ROLE_OPTIONS,
  type Role,
  useOrganizationManagement,
} from "./useOrganizationManagement";
export function IdentityPage({
  onOrganizationLeft,
  onOrganizationJoined,
}: {
  onOrganizationJoined?: (organization: {
    id: string;
    name: string;
    role?: string;
  }) => void;
  onOrganizationLeft?: (
    organizationId: string,
    remaining: { id: string; name: string; role?: string }[],
  ) => void;
}) {
  const { logout } = useAuth0();
  const {
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
    setJoinCode,
    createInvitationCode,
    previewInvitationCode,
    acceptInvitationCode,
    changeRole,
    removeMember,
    leaveOrganization,
    isLastOwner,
    isOnlyMember,
    deleteOrganization,
    resendInvitation,
    revokeInvitation,
    setSelectedOrg,
    setNewOrgName,
    setInviteEmail,
    setInviteRole,
  } = useOrganizationManagement(onOrganizationLeft, onOrganizationJoined);
  return (
    <main className="workspace-page space-y-6">
      <PageHeading
        title="Organizations"
        description="Shared workspaces, membership and invitations."
      />
      {notice && (
        <p role="status" className="text-sm text-green-700">
          {notice}
        </p>
      )}
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}

      <Card>
        <CardHeader>
          <CardTitle>Your organizations</CardTitle>
        </CardHeader>
        <CardContent>
          {organizations === null ? (
            <p className="text-muted-foreground">Loading organizations…</p>
          ) : organizations.length === 0 ? (
            <p className="text-muted-foreground">
              You do not belong to an organization yet.
            </p>
          ) : (
            <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
              {organizations.map((org) => (
                <button
                  key={org.id}
                  type="button"
                  aria-pressed={selectedOrg === org.id}
                  onClick={() => setSelectedOrg(org.id)}
                  disabled={busy !== null}
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

      <Card>
        <CardHeader>
          <CardTitle>Join organization</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <p className="text-sm text-muted-foreground">
            Paste the invitation code shared by an organization owner. No email
            is required.
          </p>
          <form
            onSubmit={previewInvitationCode}
            className="flex flex-wrap gap-3"
          >
            <input
              aria-label="Invitation code"
              required
              maxLength={128}
              value={joinCode}
              onChange={(event) => setJoinCode(event.target.value)}
              className="min-w-0 flex-1 rounded border bg-transparent px-3 py-2 text-sm"
              autoComplete="off"
            />
            <button
              type="submit"
              disabled={busy !== null}
              className="rounded-md border px-4 py-2 text-sm"
            >
              Check invitation
            </button>
          </form>
          {joinPreview && (
            <div className="space-y-3">
              <p>
                Join <strong>{joinPreview.organization_name}</strong> as{" "}
                <strong>{joinPreview.role}</strong>?
              </p>
              <button
                type="button"
                disabled={busy !== null}
                className="rounded-md bg-primary px-4 py-2 text-sm text-primary-foreground"
                onClick={() => void acceptInvitationCode()}
              >
                {busy === "join-code" ? "Joining…" : "Join organization"}
              </button>
            </div>
          )}
        </CardContent>
      </Card>

      {selected && (
        <>
          <Card>
            <CardHeader>
              <div className="flex flex-wrap items-center justify-between gap-3">
                <CardTitle>{selected.name} · Members</CardTitle>
                <button
                  type="button"
                  className="rounded-md border px-3 py-2 text-sm font-medium text-destructive disabled:opacity-50"
                  disabled={
                    busy !== null ||
                    members === null ||
                    (isLastOwner && !isOnlyMember)
                  }
                  aria-describedby={
                    isLastOwner ? "last-owner-leave-help" : undefined
                  }
                  onClick={() => void leaveOrganization()}
                >
                  {busy === "leave-organization"
                    ? "Leaving…"
                    : "Leave organization"}
                </button>
              </div>
              {canManage && (
                <div className="space-y-2">
                  <button
                    type="button"
                    className="rounded-md border px-3 py-2 text-sm font-medium text-destructive disabled:opacity-50"
                    disabled={busy !== null || members === null}
                    onClick={() => void deleteOrganization()}
                  >
                    {busy === "delete-organization"
                      ? "Deleting…"
                      : "Delete organization"}
                  </button>
                  <p className="text-sm text-muted-foreground">
                    Deletion removes access for every member and revokes robot
                    credentials. Stop and uninstall Apps first. Existing run
                    history and imported App revisions are retained.
                  </p>
                </div>
              )}
              {isLastOwner && (
                <p
                  id="last-owner-leave-help"
                  className="text-sm text-muted-foreground"
                >
                  {isOnlyMember
                    ? "Leaving as the last member will ask you to delete this organization."
                    : "Make another member an owner before leaving this organization."}
                </p>
              )}
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
                        {canManage && (
                          <th className="py-2 font-medium">Actions</th>
                        )}
                      </tr>
                    </thead>
                    <tbody>
                      {members.map((member) => (
                        <tr
                          key={member.user_id}
                          className="border-b last:border-0"
                        >
                          <td className="break-all py-3 pr-4">
                            <div className="flex items-center gap-3">
                              {member.profile_picture_url ? (
                                <img
                                  src={member.profile_picture_url}
                                  alt=""
                                  referrerPolicy="no-referrer"
                                  className="h-9 w-9 shrink-0 rounded-full border object-cover"
                                />
                              ) : (
                                <span
                                  aria-hidden="true"
                                  className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full border bg-muted text-xs"
                                >
                                  {member.name
                                    ?.trim()
                                    .charAt(0)
                                    .toUpperCase() || "?"}
                                </span>
                              )}
                              <span className="min-w-0">
                                <span className="block font-medium">
                                  {member.name || "Unnamed user"}
                                </span>
                                <span className="block text-xs text-muted-foreground">
                                  {member.email || "Email unavailable"}
                                </span>
                                <span className="mt-1 block text-xs text-muted-foreground">
                                  {member.user_id}
                                </span>
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
                                  void changeRole(
                                    member,
                                    event.target.value as Role,
                                  )
                                }
                              >
                                {ROLE_OPTIONS.map((role) => (
                                  <option key={role} value={role}>
                                    {role}
                                  </option>
                                ))}
                              </select>
                            ) : (
                              <StatusBadge
                                variant={
                                  member.role === "owner"
                                    ? "success"
                                    : "neutral"
                                }
                                label={member.role}
                              />
                            )}
                          </td>
                          {canManage && (
                            <td className="py-3">
                              {member.user_id === me?.user.id ? (
                                <span className="text-muted-foreground">
                                  You
                                </span>
                              ) : (
                                <button
                                  type="button"
                                  disabled={busy !== null}
                                  className="text-destructive underline-offset-2 hover:underline disabled:opacity-50"
                                  onClick={() => void removeMember(member)}
                                >
                                  Remove
                                </button>
                              )}
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
              {canManage && (
                <div className="space-y-3">
                  <p className="text-sm text-muted-foreground">
                    Create a code and share it directly. It expires in seven
                    days and can be used by one account.
                  </p>
                  <div className="flex flex-wrap gap-3">
                    <select
                      aria-label="Invitation code role"
                      value={inviteRole}
                      onChange={(event) =>
                        setInviteRole(event.target.value as Role)
                      }
                      disabled={busy !== null}
                      className="rounded border bg-background px-3 py-2 text-sm"
                    >
                      {ROLE_OPTIONS.map((role) => (
                        <option key={role} value={role}>
                          {role}
                        </option>
                      ))}
                    </select>
                    <button
                      type="button"
                      disabled={busy !== null}
                      onClick={() => void createInvitationCode()}
                      className="rounded-md bg-primary px-4 py-2 text-sm text-primary-foreground"
                    >
                      {busy === "invite-code"
                        ? "Creating…"
                        : "Create invitation code"}
                    </button>
                  </div>
                  {invitationCode && (
                    <label className="block space-y-2 text-sm">
                      Share this invitation code
                      <input
                        aria-label="Share this invitation code"
                        value={invitationCode}
                        readOnly
                        onFocus={(event) => event.target.select()}
                        className="w-full rounded border bg-transparent px-3 py-2 font-mono"
                      />
                      <span className="block text-muted-foreground">
                        Copy it now; the code is only shown once. The other
                        account pastes it into Join organization.
                      </span>
                      <button
                        type="button"
                        className="rounded-md border px-3 py-2"
                        onClick={() => {
                          const fallback = () => {
                            const input =
                              document.querySelector<HTMLInputElement>(
                                '[aria-label="Share this invitation code"]',
                              );
                            input?.focus();
                            input?.select();
                          };
                          if (!navigator.clipboard?.writeText) {
                            fallback();
                            return;
                          }
                          void navigator.clipboard
                            .writeText(invitationCode)
                            .catch(fallback);
                        }}
                      >
                        Copy invitation code
                      </button>
                    </label>
                  )}
                </div>
              )}
              {canManage ? (
                <details>
                  <summary className="cursor-pointer text-sm">
                    Invite by email instead
                  </summary>
                  <form
                    onSubmit={inviteMember}
                    className="grid gap-3 sm:grid-cols-[1fr_10rem_auto]"
                  >
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
                      onChange={(event) =>
                        setInviteRole(event.target.value as Role)
                      }
                    >
                      {ROLE_OPTIONS.map((role) => (
                        <option key={role} value={role}>
                          {role}
                        </option>
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
                </details>
              ) : (
                <p className="text-sm text-muted-foreground">
                  Only organization owners can send or manage invitations.
                </p>
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
                        <th className="py-2 pr-4 font-medium">Invitation</th>
                        <th className="py-2 pr-4 font-medium">Role</th>
                        <th className="py-2 pr-4 font-medium">Status</th>
                        <th className="py-2 pr-4 font-medium">Expires</th>
                        {canManage && (
                          <th className="py-2 font-medium">Actions</th>
                        )}
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
                          <tr
                            key={invitation.id}
                            className="border-b last:border-0"
                          >
                            <td className="py-3 pr-4">
                              {invitation.email || "Invitation code"}
                            </td>
                            <td className="py-3 pr-4">{invitation.role}</td>
                            <td className="py-3 pr-4">
                              <StatusBadge
                                variant={
                                  state === "accepted" ? "success" : "neutral"
                                }
                                label={state}
                              />
                            </td>
                            <td className="py-3 pr-4">
                              {new Date(invitation.expires_at).toLocaleString()}
                            </td>
                            {canManage && (
                              <td className="py-3">
                                {state === "pending" || state === "expired" ? (
                                  <span className="flex gap-3">
                                    {invitation.email && (
                                      <button
                                        type="button"
                                        disabled={busy !== null}
                                        className="text-primary underline-offset-2 hover:underline disabled:opacity-50"
                                        onClick={() =>
                                          void resendInvitation(invitation)
                                        }
                                      >
                                        Resend
                                      </button>
                                    )}
                                    <button
                                      type="button"
                                      disabled={busy !== null}
                                      className="text-destructive underline-offset-2 hover:underline disabled:opacity-50"
                                      onClick={() =>
                                        void revokeInvitation(invitation)
                                      }
                                    >
                                      Revoke
                                    </button>
                                  </span>
                                ) : (
                                  "—"
                                )}
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
        <CardHeader>
          <CardTitle>Account</CardTitle>
        </CardHeader>
        <CardContent>
          {me && (
            <p className="text-sm">
              Signed in as <span className="font-medium">{me.user.id}</span>
            </p>
          )}
          <button
            type="button"
            className="mt-4 rounded-md border px-3 py-1.5 text-sm"
            onClick={() =>
              logout({ logoutParams: { returnTo: window.location.origin } })
            }
          >
            Sign out
          </button>
        </CardContent>
      </Card>
    </main>
  );
}
