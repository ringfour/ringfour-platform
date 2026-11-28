import { useEffect, useState } from "react";
import { PageHeading } from "@/components/PageHeading";
import { StudioTime } from "@/components/StudioTime";
import { useApiToken } from "@/hooks/useApiToken";
import { apiFetch } from "@/lib/api";
import { type Robot, readJson, readPage } from "@/lib/apps";
import { studioPath, useLocation, visit } from "@/lib/location";
import { RobotAppearancePicker } from "./RobotAppearancePicker";

type Credential = {
  id: string;
  created_at: string;
  expires_at: string | null;
  revoked_at: string | null;
};
type Secret = { label: string; value: string; expires_at?: string };
const message = (error: unknown) =>
  error instanceof Error ? error.message : "Request failed";

/** Separate enrollment and access routes share the existing identity/device APIs. */
export function RobotAccessPage({
  organizationId,
  robotId,
  workspaceRole,
}: {
  organizationId: string;
  robotId?: string;
  workspaceRole?: string;
}) {
  const token = useApiToken();
  const [robots, setRobots] = useState<
    (Robot & { enrolled_at?: string | null })[]
  >([]);
  const [robot, setRobot] = useState<Robot | null>(null);
  const [credentials, setCredentials] = useState<Credential[] | null>(null);
  const [code, setCode] = useState("");
  const [name, setName] = useState("");
  const [appearance, setAppearance] = useState<string | null>("bot");
  const [imagePending, setImagePending] = useState(false);
  const [target, setTarget] = useState("");
  const [secret, setSecret] = useState<Secret | null>(null);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [busy, setBusy] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const location = useLocation();
  const returnTo = location.searchParams.get("return_to");
  const owner = workspaceRole === "owner";
  useEffect(() => {
    void refresh;
    let live = true;
    const read = async () => {
      try {
        if (robotId) {
          const current = await readJson<Robot>(token, `/v1/robots/${robotId}`);
          if (current.organization_id !== organizationId)
            throw new Error("Robot is unavailable in this workspace.");
          if (live) setRobot(current);
          const items = owner
            ? await readPage<Credential>(
                token,
                `/v1/robots/${robotId}/credentials`,
              )
            : [];
          if (live) setCredentials(items);
        } else {
          const items = await readPage<Robot>(
            token,
            `/v1/organizations/${organizationId}/robots`,
          );
          if (live) setRobots(items);
        }
      } catch (cause) {
        if (live) setError(message(cause));
      }
    };
    void read();
    return () => {
      live = false;
    };
  }, [robotId, organizationId, token, owner, refresh]);
  const request = async (path: string, body?: unknown) => {
    const response = await apiFetch(token, path, {
      method: "POST",
      ...(body === undefined
        ? {}
        : {
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(body),
          }),
    });
    return response.status === 204 ? null : response.json();
  };
  async function releaseRobot(remove: boolean) {
    if (!robot || !owner || busy) return;
    if (
      !window.confirm(
        remove
          ? `Delete ${robot.name}? Robot access will be revoked and it will be removed from this workspace. Its history is retained.`
          : `Unenroll ${robot.name}? Device access will be revoked. Restart its daemon before pairing it again.`,
      )
    )
      return;
    setBusy(true);
    setError("");
    setNotice("");
    setSecret(null);
    try {
      await apiFetch(
        token,
        `/v1/robots/${robot.id}${remove ? "" : "/unenroll"}`,
        { method: remove ? "DELETE" : "POST" },
      );
      if (remove) visit(studioPath("robots", organizationId));
      else {
        setRefresh((value) => value + 1);
        setNotice(
          "Robot unenrolled. Restart its daemon from the development panel, then use its new pairing code to enroll it again.",
        );
      }
    } catch (cause) {
      setError(message(cause));
    } finally {
      setBusy(false);
    }
  }

  async function pair(event: React.FormEvent) {
    event.preventDefault();
    if (busy || imagePending || !owner) return;
    setBusy(true);
    setError("");
    try {
      const paired = (await request(
        `/v1/organizations/${organizationId}/pairing-claims`,
        {
          code: code.trim(),
          appearance,
          ...(target ? { robot_id: target } : { name: name.trim() }),
        },
      )) as Robot;
      setCode("");
      const destination = returnTo
        ? new URL(returnTo, window.location.origin)
        : null;
      if (
        destination?.origin === window.location.origin &&
        (destination.pathname.startsWith("/hub/") ||
          (destination.pathname.startsWith("/apps/") &&
            destination.searchParams.get("install") === "1")) &&
        destination.searchParams.get("workspace") === organizationId
      ) {
        destination.searchParams.set("robot", paired.id);
        visit(destination.pathname + destination.search);
      } else visit(studioPath("robots", organizationId, paired.id));
    } catch (cause) {
      setError(message(cause));
    } finally {
      setBusy(false);
    }
  }
  async function access(
    kind: "enrollment-tokens" | "credentials" | "revoke",
    credential?: string,
  ) {
    if (busy || !owner || !robot) return;
    if (
      kind !== "enrollment-tokens" &&
      !window.confirm(
        kind === "revoke"
          ? `Revoke this credential for ${robot.name}? The device may disconnect.`
          : `Create a new credential for ${robot.name}? Save its secret before closing this page.`,
      )
    )
      return;
    setBusy(true);
    setError("");
    setSecret(null);
    try {
      const result = await request(
        `/v1/robots/${robot.id}/${kind === "revoke" ? `credentials/${credential}/revoke` : kind}`,
      );
      if (kind === "enrollment-tokens")
        setSecret({
          label: "Enrollment token",
          value: result.token,
          expires_at: result.expires_at,
        });
      if (kind === "credentials")
        setSecret({ label: "New credential secret", value: result.secret });
      setRefresh((value) => value + 1);
    } catch (cause) {
      setError(message(cause));
    } finally {
      setBusy(false);
    }
  }
  return (
    <main className="studio-container space-y-7 robot-access-page">
      <PageHeading
        title={
          robotId
            ? `${robot?.name ?? "Robot"} · Device access`
            : "Connect a robot"
        }
        description={
          robotId
            ? "Manage enrollment and the credentials this device uses to connect."
            : "Enter the pairing code shown on your device’s local page."
        }
      />
      {error && (
        <p role="alert" className="app-feedback app-feedback-error">
          {error}
        </p>
      )}
      {!owner && (
        <p>
          Only a workspace owner can enroll robots or manage device credentials.
        </p>
      )}
      {owner && !robotId && (
        <div className="grid gap-6 md:grid-cols-[1fr_1.4fr]">
          <section className="studio-surface space-y-4 p-6">
            <h2 className="font-display text-lg font-bold">
              1. Open your robot’s local page
            </h2>
            <p className="text-sm text-muted-foreground">
              Start the Ringfour daemon on the robot and open the pairing page
              at its configured local address. Copy the pairing code shown
              there.
            </p>
            <p className="text-sm text-muted-foreground">
              After pairing, the device reports its capabilities automatically.
            </p>
          </section>
          <form
            onSubmit={pair}
            className="studio-surface space-y-5 p-6 robot-access-form"
          >
            <h2 className="font-display text-lg font-bold">
              2. Connect to this workspace
            </h2>
            <label className="app-field">
              Pairing code
              <input
                autoComplete="off"
                spellCheck={false}
                required
                value={code}
                onChange={(event) => setCode(event.target.value)}
              />
            </label>
            {robots.some((item) => item.enrolled_at === null) && (
              <label className="app-field">
                Pair with
                <select
                  value={target}
                  onChange={(event) => {
                    setTarget(event.target.value);
                    setAppearance(
                      robots.find((item) => item.id === event.target.value)
                        ?.appearance ?? "bot",
                    );
                  }}
                >
                  <option value="">New robot</option>
                  {robots
                    .filter((item) => item.enrolled_at === null)
                    .map((item) => (
                      <option key={item.id} value={item.id}>
                        {item.name}
                      </option>
                    ))}
                </select>
              </label>
            )}
            {!target && (
              <label className="app-field">
                Robot name
                <input
                  required
                  maxLength={200}
                  value={name}
                  onChange={(event) => setName(event.target.value)}
                />
              </label>
            )}
            <fieldset disabled={busy}>
              <RobotAppearancePicker
                value={appearance}
                onChange={setAppearance}
                onPendingChange={setImagePending}
              />
            </fieldset>
            <button
              className="button-primary"
              type="submit"
              disabled={
                busy ||
                imagePending ||
                !code.trim() ||
                (!target && !name.trim())
              }
            >
              {busy ? "Connecting…" : "Connect robot"}
            </button>
          </form>
        </div>
      )}
      {owner && robot && (
        <section className="studio-surface space-y-4 p-6">
          <h2>Device credentials</h2>
          <div className="app-actions">
            <button
              className="button-secondary"
              type="button"
              disabled={busy}
              onClick={() => void access("enrollment-tokens")}
            >
              Issue enrollment token
            </button>
            <button
              className="button-secondary"
              type="button"
              disabled={busy}
              onClick={() => void access("credentials")}
            >
              Rotate credential
            </button>
          </div>
          {credentials === null ? (
            <p role="status">Loading credentials…</p>
          ) : !credentials.length ? (
            <p>No device credentials yet.</p>
          ) : (
            <ul className="robot-credential-list">
              {credentials.map((item) => (
                <li key={item.id}>
                  <div>
                    <strong>
                      {item.revoked_at
                        ? "Revoked"
                        : item.expires_at &&
                            Date.parse(item.expires_at) < Date.now()
                          ? "Expired"
                          : "Active"}
                    </strong>
                    <p>
                      Created <StudioTime value={item.created_at} />
                    </p>
                    <details>
                      <summary>Credential details</summary>
                      <code>{item.id}</code>
                      {item.expires_at && (
                        <p>
                          Expires <StudioTime value={item.expires_at} />
                        </p>
                      )}
                    </details>
                  </div>
                  {!item.revoked_at && (
                    <button
                      className="button-danger"
                      type="button"
                      disabled={busy}
                      onClick={() => void access("revoke", item.id)}
                    >
                      Revoke
                    </button>
                  )}
                </li>
              ))}
            </ul>
          )}
        </section>
      )}
      {owner && robot && (
        <section
          className="studio-surface space-y-4 p-6"
          aria-label="Robot enrollment and removal"
        >
          <h2>Enrollment and removal</h2>
          <p>
            Stop active App runs and wait for pending operations. Idle App
            installations are removed automatically. Unenroll keeps this robot
            record; Delete removes it from the active list. History is retained.
          </p>
          <div className="flex flex-wrap gap-3">
            <button
              type="button"
              className="button-secondary"
              disabled={busy}
              onClick={() => void releaseRobot(false)}
            >
              Unenroll robot
            </button>
            <button
              type="button"
              className="button-danger"
              disabled={busy}
              onClick={() => void releaseRobot(true)}
            >
              Delete robot
            </button>
          </div>
        </section>
      )}
      {notice && <p role="status">{notice}</p>}
      {secret && (
        <section
          className="studio-surface space-y-4 p-6"
          aria-label="One-time secret"
        >
          <h2>{secret.label} · shown once</h2>
          <p>Save this value before leaving. It cannot be retrieved again.</p>
          <code className="robot-secret" data-testid="one-time-secret">
            {secret.value}
          </code>
          {secret.expires_at && (
            <p>
              Expires <StudioTime value={secret.expires_at} />
            </p>
          )}
          <button
            className="button-secondary"
            type="button"
            onClick={() => setSecret(null)}
          >
            Dismiss secret
          </button>
        </section>
      )}
    </main>
  );
}
