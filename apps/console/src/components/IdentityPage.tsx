import { useAuth0 } from "@auth0/auth0-react";
import { useCallback, useEffect, useState } from "react";

import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { StatusBadge } from "@/components/ui/status-badge";
import { apiFetch } from "@/lib/api";

type User = { id: string; created_at: string };
type Membership = {
  organization_id: string;
  user_id: string;
  role: "owner" | "developer" | "operator";
  created_at: string;
};
type MeResponse = { user: User; memberships: Membership[] };
type OrganizationItem = {
  id: string;
  name: string;
  role: Membership["role"];
  created_at: string;
};

export function IdentityPage() {
  const { getAccessTokenSilently, logout } = useAuth0();
  const getToken = useCallback(async (): Promise<string> => {
    const token = await getAccessTokenSilently({
      authorizationParams: { audience: import.meta.env.VITE_AUTH0_AUDIENCE },
    });
    if (!token) {
      throw new Error("No access token available");
    }
    return token;
  }, [getAccessTokenSilently]);

  const [me, setMe] = useState<MeResponse | null>(null);
  const [organizations, setOrganizations] = useState<OrganizationItem[] | null>(
    null,
  );
  const [members, setMembers] = useState<Membership[] | null>(null);
  const [selectedOrg, setSelectedOrg] = useState<string | null>(null);
  const [newOrgName, setNewOrgName] = useState("");
  const [error, setError] = useState<string | null>(null);

  const loadIdentity = useCallback(async () => {
    try {
      const [meResponse, orgsResponse] = await Promise.all([
        apiFetch(getToken, "/v1/me").then((r) => r.json()),
        apiFetch(getToken, "/v1/organizations").then((r) => r.json()),
      ]);
      setMe(meResponse as MeResponse);
      setOrganizations((orgsResponse as { items: OrganizationItem[] }).items);
      setError(null);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Request failed");
    }
  }, [getToken]);

  useEffect(() => {
    void loadIdentity();
  }, [loadIdentity]);

  const createOrganization = async (event: React.FormEvent) => {
    event.preventDefault();
    try {
      await apiFetch(getToken, "/v1/organizations", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name: newOrgName }),
      });
      setNewOrgName("");
      setError(null);
      await loadIdentity();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Request failed");
    }
  };

  const inspectMembers = async (orgId: string) => {
    setSelectedOrg(orgId);
    try {
      const response = await apiFetch(
        getToken,
        `/v1/organizations/${orgId}/members`,
      ).then((r) => r.json());
      setMembers((response as { items: Membership[] }).items);
      setError(null);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Request failed");
    }
  };

  return (
    <main className="mx-auto max-w-2xl space-y-6 p-6">
      <Card>
        <CardHeader>
          <CardTitle>Identity</CardTitle>
        </CardHeader>
        <CardContent>
          {me === null ? (
            <p className="text-muted-foreground">Loading…</p>
          ) : (
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

      <Card>
        <CardHeader>
          <CardTitle>Organizations</CardTitle>
        </CardHeader>
        <CardContent>
          {organizations === null ? (
            <p className="text-muted-foreground">Loading…</p>
          ) : (
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-left text-muted-foreground">
                  <th className="py-2 font-medium">Name</th>
                  <th className="py-2 font-medium">ID</th>
                  <th className="py-2 font-medium">Role</th>
                </tr>
              </thead>
              <tbody>
                {organizations.map((org) => (
                  <tr key={org.id} className="border-b last:border-0">
                    <td className="py-2">
                      <button
                        type="button"
                        className="font-medium text-primary underline-offset-2 hover:underline"
                        onClick={() => inspectMembers(org.id)}
                      >
                        {org.name}
                      </button>
                    </td>
                    <td className="py-2 text-muted-foreground">{org.id}</td>
                    <td className="py-2">
                      <StatusBadge
                        variant={org.role === "owner" ? "success" : "neutral"}
                        label={org.role}
                      />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          <form onSubmit={createOrganization} className="mt-4 flex gap-2">
            <input
              className="flex-1 rounded border bg-transparent px-3 py-1.5 text-sm"
              placeholder="New organization name"
              value={newOrgName}
              onChange={(event) => setNewOrgName(event.target.value)}
            />
            <button
              type="submit"
              className="rounded-md bg-primary px-4 py-1.5 text-sm font-medium text-primary-foreground"
            >
              Create
            </button>
          </form>
        </CardContent>
      </Card>

      {selectedOrg !== null && (
        <Card>
          <CardHeader>
            <CardTitle>Members</CardTitle>
          </CardHeader>
          <CardContent>
            {members === null ? (
              <p className="text-muted-foreground">Loading…</p>
            ) : (
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b text-left text-muted-foreground">
                    <th className="py-2 font-medium">User</th>
                    <th className="py-2 font-medium">Role</th>
                  </tr>
                </thead>
                <tbody>
                  {members.map((member) => (
                    <tr key={member.user_id} className="border-b last:border-0">
                      <td className="py-2">{member.user_id}</td>
                      <td className="py-2">
                        <StatusBadge
                          variant={
                            member.role === "owner" ? "success" : "neutral"
                          }
                          label={member.role}
                        />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </CardContent>
        </Card>
      )}

      {error !== null && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
    </main>
  );
}
