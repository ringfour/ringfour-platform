import { useEffect, useState } from "react";
import { apiFetch } from "@/lib/api";
import { type App, type Installation, readJson } from "@/lib/apps";
import { StudioSection } from "@/views/StudioPage";

type Session = {
  session_id: string;
  installation_id: string;
  owner_user_id: string;
  expires_at: string;
  generation: number;
  archive_sha256: string | null;
};
export function RobotDevelopment({
  robotId,
  token,
  installations,
  apps,
  activeRun,
  onChange,
}: {
  robotId: string;
  token: () => Promise<string>;
  installations: Installation[];
  apps: Record<string, App>;
  activeRun: boolean;
  onChange: () => void;
}) {
  const [session, setSession] = useState<Session | null>(null);
  const [installation, setInstallation] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [loaded, setLoaded] = useState(false);
  useEffect(() => {
    let live = true;
    const refresh = async () => {
      try {
        const result = await readJson<{ session: Session | null }>(
          token,
          `/v1/robots/${robotId}/app-development`,
        );
        if (live) {
          setSession(result.session);
          setLoaded(true);
        }
      } catch (e) {
        if (live)
          setError(
            e instanceof Error ? e.message : "Cannot read development mode",
          );
      }
    };
    void refresh();
    const timer = window.setInterval(() => void refresh(), 5000);
    return () => {
      live = false;
      window.clearInterval(timer);
    };
  }, [robotId, token]);
  const installed = installations.filter(
    (item) => item.installed && !item.removed_at,
  );
  const selected = installation || installed[0]?.id || "";
  const change = async () => {
    setBusy(true);
    setError("");
    try {
      const response = await apiFetch(
        token,
        `/v1/robots/${robotId}/app-development${session ? `?expected_session_id=${session.session_id}` : ""}`,
        {
          method: session ? "DELETE" : "POST",
          ...(session
            ? {}
            : {
                headers: {
                  "Idempotency-Key": crypto.randomUUID(),
                  "Content-Type": "application/json",
                },
                body: JSON.stringify({
                  installation_id: selected,
                  ttl_seconds: 3600,
                }),
              }),
        },
      );
      setSession(session ? null : await response.json());
      onChange();
    } catch (e) {
      setError(
        e instanceof Error ? e.message : "Cannot change development mode",
      );
    } finally {
      setBusy(false);
    }
  };
  return (
    <StudioSection
      title="Development mode"
      titleId="robot-development-heading"
      description="Sync code over your LAN. Dependencies and models stay in the installed development image."
    >
      {error && (
        <p role="alert" className="text-destructive">
          {error}
        </p>
      )}
      {session ? (
        <>
          <p>
            Session {session.session_id} · source generation{" "}
            {session.generation}
          </p>
          <p>
            Expires {new Date(session.expires_at).toLocaleString()}. Ending the
            session stops its development run when the daemon observes closure.
          </p>
          <p>In your App directory:</p>
          <pre>
            <code>{`ringfour-cli app dev connect --robot ${robotId}\nringfour-cli app dev sync\nringfour-cli app dev restart --follow`}</code>
          </pre>
          <button type="button" disabled={busy} onClick={() => void change()}>
            End development session
          </button>
        </>
      ) : (
        <>
          <p>
            Install an image built with <code>app build --development</code>{" "}
            first. The robot must be idle before entering development mode.
          </p>
          <label>
            Development App
            <select
              value={selected}
              onChange={(event) => setInstallation(event.target.value)}
              disabled={busy}
            >
              {!installed.length && <option value="">No installed Apps</option>}
              {installed.map((item) => (
                <option key={item.id} value={item.id}>
                  {apps[item.app_id]?.name || item.app_id}
                </option>
              ))}
            </select>
          </label>
          <button
            type="button"
            disabled={busy || !loaded || !selected || activeRun}
            onClick={() => void change()}
          >
            Enable development mode
          </button>
          {activeRun && (
            <p>Stop the current App before enabling development mode.</p>
          )}
        </>
      )}
    </StudioSection>
  );
}
