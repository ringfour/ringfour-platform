import { useEffect, useState } from "react";
import { PageHeading } from "@/components/PageHeading";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { useApiToken } from "@/hooks/useApiToken";
import { apiFetch } from "@/lib/api";

type Preview = {
  organization_id: string;
  organization_name: string;
  email: string | null;
  role: "owner" | "developer" | "operator";
  expires_at: string;
};

export function InvitationAcceptancePage({ token }: { token: string }) {
  const getToken = useApiToken();
  const [preview, setPreview] = useState<Preview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [accepted, setAccepted] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    apiFetch(getToken, "/v1/invitations/preview", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ token }),
    })
      .then((response) => response.json() as Promise<Preview>)
      .then(setPreview)
      .catch((cause: unknown) =>
        setError(
          cause instanceof Error ? cause.message : "Unable to load invitation.",
        ),
      );
  }, [getToken, token]);

  const accept = async () => {
    setBusy(true);
    setError(null);
    try {
      await apiFetch(getToken, "/v1/invitations/accept", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ token }),
      });
      setAccepted(true);
      window.history.replaceState({}, "", window.location.pathname);
    } catch (cause) {
      setError(
        cause instanceof Error ? cause.message : "Unable to accept invitation.",
      );
    } finally {
      setBusy(false);
    }
  };

  return (
    <main className="workspace-page mx-auto max-w-2xl space-y-6">
      <PageHeading
        title="Organization invitation"
        description="Review and accept your Ringfour workspace invitation."
      />
      <Card>
        <CardHeader>
          <CardTitle>
            {accepted ? "You joined the organization" : "Invitation details"}
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          {preview ? (
            <div className="space-y-2 text-sm">
              <p>
                <span className="text-muted-foreground">Organization: </span>
                <strong>{preview.organization_name}</strong>
              </p>
              <p>
                <span className="text-muted-foreground">Invited email: </span>
                {preview.email || "Anyone with this code (no email required)"}
              </p>
              <p>
                <span className="text-muted-foreground">Role: </span>
                {preview.role}
              </p>
              <p className="text-muted-foreground">
                Expires {new Date(preview.expires_at).toLocaleString()}
              </p>
            </div>
          ) : !error ? (
            <p className="text-muted-foreground">Loading invitation…</p>
          ) : null}
          {accepted ? (
            <button
              type="button"
              className="rounded-md bg-primary px-4 py-2 text-sm font-medium text-primary-foreground"
              onClick={() => window.location.replace(window.location.origin)}
            >
              Open Console
            </button>
          ) : preview ? (
            <button
              type="button"
              disabled={busy}
              className="rounded-md bg-primary px-4 py-2 text-sm font-medium text-primary-foreground disabled:opacity-50"
              onClick={() => void accept()}
            >
              {busy ? "Accepting…" : "Accept invitation"}
            </button>
          ) : null}
          {error && (
            <p role="alert" className="text-sm text-destructive">
              {error}
            </p>
          )}
        </CardContent>
      </Card>
    </main>
  );
}
