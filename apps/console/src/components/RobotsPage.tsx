import { useAuth0 } from "@auth0/auth0-react";
import { useCallback, useEffect, useState } from "react";

import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { StatusBadge } from "@/components/ui/status-badge";
import { apiFetch } from "@/lib/api";

type OrganizationItem = {
  id: string;
  name: string;
  role: "owner" | "developer" | "operator";
};

type Robot = {
  id: string;
  organization_id: string;
  name: string;
  created_at: string;
  enrolled_at: string | null;
  profile_ref: string | null;
};

type Credential = {
  id: string;
  robot_id: string;
  created_at: string;
  expires_at: string | null;
  revoked_at: string | null;
};

type RobotStatus = {
  robot_id: string;
  online: boolean;
  last_seen_at: string | null;
  daemon_version: string | null;
  protocol_version: number | null;
};

// A secret shown exactly once (enrollment token or rotated credential
// secret). It lives only in this component's state until dismissed.
type ShownSecret = { kind: string; value: string; expires_at?: string };

export function RobotsPage() {
  const { getAccessTokenSilently } = useAuth0();
  const getToken = useCallback(async (): Promise<string> => {
    const token = await getAccessTokenSilently({
      authorizationParams: { audience: import.meta.env.VITE_AUTH0_AUDIENCE },
    });
    if (!token) {
      throw new Error("No access token available");
    }
    return token;
  }, [getAccessTokenSilently]);

  const [organizations, setOrganizations] = useState<OrganizationItem[] | null>(
    null,
  );
  const [selectedOrg, setSelectedOrg] = useState<OrganizationItem | null>(null);
  const [robots, setRobots] = useState<Robot[] | null>(null);
  const [selectedRobot, setSelectedRobot] = useState<Robot | null>(null);
  const [credentials, setCredentials] = useState<Credential[] | null>(null);
  const [robotStatus, setRobotStatus] = useState<RobotStatus | null>(null);
  const [newRobotName, setNewRobotName] = useState("");
  const [shownSecret, setShownSecret] = useState<ShownSecret | null>(null);
  const [error, setError] = useState<string | null>(null);

  const report = useCallback(
    (cause: unknown) =>
      setError(cause instanceof Error ? cause.message : "Request failed"),
    [],
  );

  useEffect(() => {
    apiFetch(getToken, "/v1/organizations")
      .then((r) => r.json())
      .then((body) => {
        const items = (body as { items: OrganizationItem[] }).items;
        setOrganizations(items);
        setSelectedOrg((current) => current ?? items[0] ?? null);
      })
      .catch(report);
  }, [getToken, report]);

  const loadRobots = useCallback(
    async (orgId: string) => {
      try {
        const body = await apiFetch(
          getToken,
          `/v1/organizations/${orgId}/robots`,
        ).then((r) => r.json());
        setRobots((body as { items: Robot[] }).items);
        setError(null);
      } catch (cause) {
        report(cause);
      }
    },
    [getToken, report],
  );

  useEffect(() => {
    setRobots(null);
    setSelectedRobot(null);
    setCredentials(null);
    setShownSecret(null);
    if (selectedOrg !== null) {
      void loadRobots(selectedOrg.id);
    }
  }, [selectedOrg, loadRobots]);

  // Observed device status: poll while a robot is selected. Read-only; any
  // member may view it. The server derives online/offline from its own
  // receive-time freshness window, so this reflects reality even when the
  // device never says goodbye.
  useEffect(() => {
    if (selectedRobot === null) {
      setRobotStatus(null);
      return;
    }
    let cancelled = false;
    const load = async () => {
      try {
        const body = await apiFetch(
          getToken,
          `/v1/robots/${selectedRobot.id}/status`,
        ).then((response) => response.json());
        if (!cancelled) {
          setRobotStatus(body as RobotStatus);
        }
      } catch {
        // Keep the last known status; the next poll retries.
      }
    };
    void load();
    const timer = setInterval(() => void load(), 5000);
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, [selectedRobot, getToken]);

  const inspectRobot = async (robot: Robot) => {
    setSelectedRobot(robot);
    setShownSecret(null);
    try {
      const body = await apiFetch(
        getToken,
        `/v1/robots/${robot.id}/credentials`,
      ).then((r) => r.json());
      setCredentials((body as { items: Credential[] }).items);
      setError(null);
    } catch (cause) {
      report(cause);
    }
  };

  const createRobot = async (event: React.FormEvent) => {
    event.preventDefault();
    if (selectedOrg === null) {
      return;
    }
    try {
      await apiFetch(getToken, `/v1/organizations/${selectedOrg.id}/robots`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name: newRobotName, profile_ref: null }),
      });
      setNewRobotName("");
      setError(null);
      await loadRobots(selectedOrg.id);
    } catch (cause) {
      report(cause);
    }
  };

  const issueToken = async (robotId: string) => {
    try {
      const body = await apiFetch(
        getToken,
        `/v1/robots/${robotId}/enrollment-tokens`,
        { method: "POST" },
      ).then((r) => r.json());
      const token = body as { token: string; expires_at: string };
      setShownSecret({
        kind: "Enrollment token",
        value: token.token,
        expires_at: token.expires_at,
      });
      setError(null);
    } catch (cause) {
      report(cause);
    }
  };

  const rotateCredential = async (robotId: string) => {
    try {
      const body = await apiFetch(
        getToken,
        `/v1/robots/${robotId}/credentials`,
        {
          method: "POST",
        },
      ).then((r) => r.json());
      const rotated = body as { credential: Credential; secret: string };
      setError(null);
      await inspectRobot(selectedRobot as Robot);
      setShownSecret({ kind: "New credential secret", value: rotated.secret });
    } catch (cause) {
      report(cause);
    }
  };

  const revokeCredential = async (robotId: string, credentialId: string) => {
    try {
      await apiFetch(
        getToken,
        `/v1/robots/${robotId}/credentials/${credentialId}/revoke`,
        { method: "POST" },
      );
      setError(null);
      await inspectRobot(selectedRobot as Robot);
    } catch (cause) {
      report(cause);
    }
  };

  const isOwner = selectedOrg?.role === "owner";

  return (
    <main className="mx-auto max-w-2xl space-y-6 p-6">
      <Card>
        <CardHeader>
          <CardTitle>Robots</CardTitle>
        </CardHeader>
        <CardContent>
          {organizations === null ? (
            <p className="text-muted-foreground">Loading…</p>
          ) : organizations.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              You belong to no organization yet; create one on the Identity
              view.
            </p>
          ) : (
            <>
              <label className="mb-4 flex items-center gap-2 text-sm">
                <span className="text-muted-foreground">Organization</span>
                <select
                  className="rounded border bg-transparent px-2 py-1.5 text-sm"
                  value={selectedOrg?.id ?? ""}
                  onChange={(event) =>
                    setSelectedOrg(
                      organizations.find(
                        (org) => org.id === event.target.value,
                      ) ?? null,
                    )
                  }
                >
                  {organizations.map((org) => (
                    <option key={org.id} value={org.id}>
                      {org.name} ({org.role})
                    </option>
                  ))}
                </select>
              </label>
              {robots === null ? (
                <p className="text-muted-foreground">Loading…</p>
              ) : robots.length === 0 ? (
                <p className="text-sm text-muted-foreground">
                  No robots in this organization yet.
                </p>
              ) : (
                <table className="w-full text-sm">
                  <thead>
                    <tr className="border-b text-left text-muted-foreground">
                      <th className="py-2 font-medium">Name</th>
                      <th className="py-2 font-medium">Status</th>
                      <th className="py-2 font-medium">Created</th>
                    </tr>
                  </thead>
                  <tbody>
                    {robots.map((robot) => (
                      <tr key={robot.id} className="border-b last:border-0">
                        <td className="py-2">
                          <button
                            type="button"
                            className="font-medium text-primary underline-offset-2 hover:underline"
                            onClick={() => inspectRobot(robot)}
                          >
                            {robot.name}
                          </button>
                        </td>
                        <td className="py-2">
                          {robot.enrolled_at === null ? (
                            <StatusBadge
                              variant="neutral"
                              label="not enrolled"
                            />
                          ) : (
                            <StatusBadge variant="success" label="enrolled" />
                          )}
                        </td>
                        <td className="py-2 text-muted-foreground">
                          {robot.created_at}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
              {isOwner && (
                <form onSubmit={createRobot} className="mt-4 flex gap-2">
                  <input
                    className="flex-1 rounded border bg-transparent px-3 py-1.5 text-sm"
                    placeholder="New robot name"
                    value={newRobotName}
                    onChange={(event) => setNewRobotName(event.target.value)}
                  />
                  <button
                    type="submit"
                    className="rounded-md bg-primary px-4 py-1.5 text-sm font-medium text-primary-foreground"
                  >
                    Create
                  </button>
                </form>
              )}
            </>
          )}
        </CardContent>
      </Card>

      {shownSecret !== null && (
        <Card>
          <CardHeader>
            <CardTitle>{shownSecret.kind} — shown once</CardTitle>
          </CardHeader>
          <CardContent>
            <p
              data-testid="one-time-secret"
              className="break-all rounded border bg-muted px-3 py-2 font-mono text-sm"
            >
              {shownSecret.value}
            </p>
            <p className="mt-2 text-sm text-muted-foreground">
              {shownSecret.expires_at !== undefined
                ? `Single-use; expires ${shownSecret.expires_at}. `
                : ""}
              This value is never retrievable again. Pass it to `ringfour-cli
              daemon enroll` or store it nowhere at all.
            </p>
            <button
              type="button"
              className="mt-3 rounded-md border px-3 py-1.5 text-sm"
              onClick={() => setShownSecret(null)}
            >
              Dismiss
            </button>
          </CardContent>
        </Card>
      )}

      {selectedRobot !== null && (
        <Card>
          <CardHeader>
            <CardTitle>Robot detail</CardTitle>
          </CardHeader>
          <CardContent>
            <dl className="space-y-1 text-sm">
              <div className="flex gap-2">
                <dt className="w-28 text-muted-foreground">ID</dt>
                <dd className="font-mono">{selectedRobot.id}</dd>
              </div>
              <div className="flex gap-2">
                <dt className="w-28 text-muted-foreground">Name</dt>
                <dd>{selectedRobot.name}</dd>
              </div>
              <div className="flex gap-2">
                <dt className="w-28 text-muted-foreground">Enrolled</dt>
                <dd>{selectedRobot.enrolled_at ?? "no"}</dd>
              </div>
              <div className="flex gap-2">
                <dt className="w-28 text-muted-foreground">Profile ref</dt>
                <dd>{selectedRobot.profile_ref ?? "-"}</dd>
              </div>
            </dl>

            <h3 className="mt-6 mb-2 text-sm font-medium">Device status</h3>
            {robotStatus === null ? (
              <p className="text-muted-foreground">Loading…</p>
            ) : (
              <dl className="space-y-1 text-sm" data-testid="device-status">
                <div className="flex gap-2">
                  <dt className="w-28 text-muted-foreground">Presence</dt>
                  <dd>
                    {robotStatus.online ? (
                      <StatusBadge variant="success" label="online" />
                    ) : (
                      <StatusBadge variant="neutral" label="offline" />
                    )}
                  </dd>
                </div>
                <div className="flex gap-2">
                  <dt className="w-28 text-muted-foreground">Last seen</dt>
                  <dd>{robotStatus.last_seen_at ?? "never"}</dd>
                </div>
                <div className="flex gap-2">
                  <dt className="w-28 text-muted-foreground">Daemon version</dt>
                  <dd>{robotStatus.daemon_version ?? "-"}</dd>
                </div>
                <div className="flex gap-2">
                  <dt className="w-28 text-muted-foreground">Protocol</dt>
                  <dd>
                    {robotStatus.protocol_version === null
                      ? "-"
                      : `v${robotStatus.protocol_version}`}
                  </dd>
                </div>
              </dl>
            )}
            {isOwner && (
              <div className="mt-4 flex gap-2">
                <button
                  type="button"
                  className="rounded-md bg-primary px-3 py-1.5 text-sm font-medium text-primary-foreground"
                  onClick={() => issueToken(selectedRobot.id)}
                >
                  Issue enrollment token
                </button>
                <button
                  type="button"
                  className="rounded-md border px-3 py-1.5 text-sm"
                  onClick={() => rotateCredential(selectedRobot.id)}
                >
                  Rotate credential
                </button>
              </div>
            )}

            <h3 className="mt-6 mb-2 text-sm font-medium">Credentials</h3>
            {credentials === null ? (
              <p className="text-muted-foreground">Loading…</p>
            ) : credentials.length === 0 ? (
              <p className="text-sm text-muted-foreground">
                No credentials yet — enroll a device with a token.
              </p>
            ) : (
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b text-left text-muted-foreground">
                    <th className="py-2 font-medium">ID</th>
                    <th className="py-2 font-medium">State</th>
                    <th className="py-2 font-medium">Expires</th>
                    {isOwner && <th className="py-2 font-medium" />}
                  </tr>
                </thead>
                <tbody>
                  {credentials.map((credential) => (
                    <tr key={credential.id} className="border-b last:border-0">
                      <td className="py-2 font-mono text-xs">
                        {credential.id}
                      </td>
                      <td className="py-2">
                        {credential.revoked_at !== null ? (
                          <StatusBadge variant="danger" label="revoked" />
                        ) : (
                          <StatusBadge variant="success" label="active" />
                        )}
                      </td>
                      <td className="py-2 text-muted-foreground">
                        {credential.expires_at ?? "-"}
                      </td>
                      {isOwner && (
                        <td className="py-2 text-right">
                          {credential.revoked_at === null && (
                            <button
                              type="button"
                              className="rounded-md border border-danger px-2 py-1 text-xs text-danger"
                              onClick={() =>
                                revokeCredential(
                                  selectedRobot.id,
                                  credential.id,
                                )
                              }
                            >
                              Revoke
                            </button>
                          )}
                        </td>
                      )}
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
