import { useAuth0 } from "@auth0/auth0-react";
import { useCallback, useEffect, useState } from "react";
import { loadOverview, type OverviewData } from "@/components/overview-data";
import { RunTimeline } from "@/components/RunTimeline";
import type { Token } from "@/lib/apps";
import "./app-workflows.css";

export function AppTimelinePage({
  organizationId,
}: {
  organizationId: string;
}) {
  const { getAccessTokenSilently } = useAuth0();
  const token = useCallback<Token>(async () => {
    const value = await getAccessTokenSilently({
      authorizationParams: { audience: import.meta.env.VITE_AUTH0_AUDIENCE },
    });
    if (!value) throw new Error("Sign in required");
    return value;
  }, [getAccessTokenSilently]);
  const [data, setData] = useState<OverviewData | null>(null);
  const [error, setError] = useState("");
  useEffect(() => {
    let live = true;
    setData(null);
    loadOverview(organizationId, token)
      .then((value) => {
        if (live) {
          setData(value);
          setError("");
        }
      })
      .catch((cause) => {
        if (live)
          setError(
            cause instanceof Error
              ? cause.message
              : "App timeline is unavailable.",
          );
      });
    return () => {
      live = false;
    };
  }, [organizationId, token]);
  return (
    <main className="app-workflow-page">
      <header className="app-workflow-heading">
        <div>
          <p className="app-eyebrow">Workspace event history</p>
          <h1>App timeline</h1>
          <p>
            Run intervals and attributed App or robot events. Times use your
            local timezone.
          </p>
        </div>
      </header>
      {error && (
        <p role="alert" className="app-feedback app-feedback-error">
          {error}
        </p>
      )}
      {data ? (
        <RunTimeline
          robots={data.robots}
          runs={data.runs}
          workspace={organizationId}
          token={token}
        />
      ) : (
        <section className="app-panel">
          <p>{error || "Loading App timeline…"}</p>
        </section>
      )}
    </main>
  );
}
