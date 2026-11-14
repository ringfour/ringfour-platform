import { useAuth0 } from "@auth0/auth0-react";
import { useCallback, useEffect, useState } from "react";

import { PageHeading } from "@/components/PageHeading";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { StatusBadge } from "@/components/ui/status-badge";
import { apiFetch, apiFetchAll } from "@/lib/api";

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

export function RobotsPage({
  preferredOrganizationId = "",
  preferredRobotId,
  onOrganizationChange,
  onOpenRobot,
  onRobotsChanged,
}: {
  preferredOrganizationId?: string;
  preferredRobotId?: string;
  onOrganizationChange?: (id: string) => void;
  onOpenRobot?: (id: string) => void;
  onRobotsChanged?: () => void;
} = {}) {
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
  const [pairingCode, setPairingCode] = useState("");
  const [pairingName, setPairingName] = useState("");
  const [pairingTarget, setPairingTarget] = useState("");
  const [pairingBusy, setPairingBusy] = useState(false);
  const [pairingMessage, setPairingMessage] = useState<string | null>(null);
  const [shownSecret, setShownSecret] = useState<ShownSecret | null>(null);
  const [error, setError] = useState<string | null>(null);

  const report = useCallback(
    (cause: unknown) =>
      setError(cause instanceof Error ? cause.message : "Request failed"),
    [],
  );

  useEffect(() => {
    apiFetchAll<OrganizationItem>(getToken, "/v1/organizations")
      .then((items) => {
        setOrganizations(items);
        setSelectedOrg(
          (current) =>
            current ??
            items.find((item) => item.id === preferredOrganizationId) ??
            items[0] ??
            null,
        );
      })
      .catch(report);
  }, [getToken, report, preferredOrganizationId]);

  const loadRobots = useCallback(
    async (orgId: string) => {
      try {
        const items = await apiFetchAll<Robot>(
          getToken,
          `/v1/organizations/${orgId}/robots`,
        );
        setRobots(items);
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
    setPairingCode("");
    setPairingName("");
    setPairingTarget("");
    setPairingMessage(null);
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

  const inspectRobot = useCallback(
    async (robot: Robot) => {
      setSelectedRobot(robot);
      setRobotStatus(null);
      setCredentials(null);
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
    },
    [getToken, report],
  );

  useEffect(() => {
    const target = robots?.find((robot) => robot.id === preferredRobotId);
    if (target && selectedRobot?.id !== target.id) void inspectRobot(target);
  }, [robots, preferredRobotId, selectedRobot?.id, inspectRobot]);

  const pairRobot = async (event: React.FormEvent) => {
    event.preventDefault();
    if (selectedOrg === null || pairingBusy) {
      return;
    }
    const orgId = selectedOrg.id;
    setPairingBusy(true);
    setPairingMessage(null);
    setError(null);
    try {
      const response = await apiFetch(
        getToken,
        `/v1/organizations/${orgId}/pairing-claims`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            code: pairingCode.trim(),
            ...(pairingTarget
              ? { robot_id: pairingTarget }
              : { name: pairingName.trim() }),
          }),
        },
      );
      const robot = (await response.json()) as Robot;
      setPairingCode("");
      setPairingName("");
      setPairingTarget("");
      setPairingMessage(
        `${robot.name} paired. The device should connect shortly.`,
      );
      setError(null);
      await loadRobots(orgId);
      onRobotsChanged?.();
      if (selectedRobot?.id === robot.id) {
        await inspectRobot(robot);
      }
    } catch (cause) {
      report(cause);
    } finally {
      setPairingBusy(false);
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
    <main className="workspace-page space-y-6">
      <PageHeading
        title="Fleet"
        description="Connect devices and inspect your robots."
      />
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
                  disabled={pairingBusy}
                  onChange={(event) => {
                    setSelectedOrg(
                      organizations.find(
                        (org) => org.id === event.target.value,
                      ) ?? null,
                    );
                    onOrganizationChange?.(event.target.value);
                  }}
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
                            onClick={() =>
                              onOpenRobot
                                ? onOpenRobot(robot.id)
                                : inspectRobot(robot)
                            }
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
                <form
                  onSubmit={pairRobot}
                  className="mt-6 space-y-3 border-t pt-4"
                >
                  <h3 className="text-sm font-medium">Add a robot</h3>
                  <p className="text-sm text-muted-foreground">
                    Enter the pairing code shown on the device page.
                  </p>
                  <label className="block space-y-1 text-sm">
                    <span>Pairing code</span>
                    <input
                      className="w-full rounded border bg-transparent px-3 py-1.5 text-sm"
                      value={pairingCode}
                      onChange={(event) => setPairingCode(event.target.value)}
                      autoComplete="off"
                      spellCheck={false}
                      required
                    />
                  </label>
                  {robots?.some((robot) => robot.enrolled_at === null) && (
                    <label className="block space-y-1 text-sm">
                      <span>Pair with</span>
                      <select
                        className="w-full rounded border bg-transparent px-3 py-1.5 text-sm"
                        value={pairingTarget}
                        onChange={(event) =>
                          setPairingTarget(event.target.value)
                        }
                      >
                        <option value="">New robot</option>
                        {robots
                          .filter((robot) => robot.enrolled_at === null)
                          .map((robot) => (
                            <option key={robot.id} value={robot.id}>
                              {robot.name}
                            </option>
                          ))}
                      </select>
                    </label>
                  )}
                  {!pairingTarget && (
                    <label className="block space-y-1 text-sm">
                      <span>Robot name</span>
                      <input
                        className="w-full rounded border bg-transparent px-3 py-1.5 text-sm"
                        value={pairingName}
                        onChange={(event) => setPairingName(event.target.value)}
                        maxLength={200}
                        required
                      />
                    </label>
                  )}
                  <button
                    type="submit"
                    disabled={
                      pairingBusy ||
                      !pairingCode.trim() ||
                      (!pairingTarget && !pairingName.trim())
                    }
                    className="rounded-md bg-primary px-4 py-1.5 text-sm font-medium text-primary-foreground disabled:opacity-50"
                  >
                    {pairingBusy ? "Pairing…" : "Add robot"}
                  </button>
                  {pairingMessage && (
                    <p role="status" className="text-sm text-ready-text">
                      {pairingMessage}
                    </p>
                  )}
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
                <dt className="w-28 text-muted-foreground">Created</dt>
                <dd>{selectedRobot.created_at}</dd>
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
                No credentials yet — add a device using its pairing code.
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
