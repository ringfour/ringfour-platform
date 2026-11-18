import { useAuth0 } from "@auth0/auth0-react";
import { useCallback, useEffect, useState } from "react";

import { PageHeading } from "@/components/PageHeading";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { apiFetch } from "@/lib/api";

type UserProfile = {
  id: string;
  created_at: string;
  name: string | null;
  email: string | null;
  profile_picture_url: string | null;
  phone_number: string | null;
};

export function ProfilePage() {
  const { getAccessTokenSilently } = useAuth0();
  const getToken = useCallback(async () => {
    const token = await getAccessTokenSilently({
      authorizationParams: { audience: import.meta.env.VITE_AUTH0_AUDIENCE },
    });
    if (!token) throw new Error("No access token available");
    return token;
  }, [getAccessTokenSilently]);

  const [profile, setProfile] = useState<UserProfile | null>(null);
  const [name, setName] = useState("");
  const [picture, setPicture] = useState("");
  const [phone, setPhone] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const loadProfile = useCallback(async () => {
    try {
      const response = await apiFetch(getToken, "/v1/me");
      const result = (await response.json()) as { user: UserProfile };
      setProfile(result.user);
      setName(result.user.name ?? "");
      setPicture(result.user.profile_picture_url ?? "");
      setPhone(result.user.phone_number ?? "");
      setError(null);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Request failed");
    }
  }, [getToken]);

  useEffect(() => {
    void loadProfile();
  }, [loadProfile]);

  const save = async (event: React.FormEvent) => {
    event.preventDefault();
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const response = await apiFetch(getToken, "/v1/me", {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name: name.trim() || null,
          profile_picture_url: picture.trim() || null,
          phone_number: phone.trim() || null,
        }),
      });
      setProfile((await response.json()) as UserProfile);
      setNotice("Profile saved.");
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Request failed");
    } finally {
      setBusy(false);
    }
  };

  return (
    <main className="workspace-page space-y-6">
      <PageHeading title="My profile" description="Manage the details shown to people in your organizations." />
      <Card>
        <CardHeader><CardTitle>Personal details</CardTitle></CardHeader>
        <CardContent>
          {profile === null && !error ? <p className="text-muted-foreground">Loading profile…</p> : (
            <form onSubmit={save} className="max-w-xl space-y-5">
              <div className="flex items-center gap-4">
                {picture ? (
                  <img src={picture} alt="Profile preview" referrerPolicy="no-referrer" className="h-16 w-16 rounded-full border object-cover" />
                ) : (
                  <div aria-hidden="true" className="flex h-16 w-16 items-center justify-center rounded-full border bg-muted text-xl">{name.trim().charAt(0).toUpperCase() || "?"}</div>
                )}
                <div className="min-w-0">
                  <p className="font-medium">{name.trim() || "Your name"}</p>
                  <p className="break-all text-sm text-muted-foreground">{profile?.email ?? "No verified email available"}</p>
                </div>
              </div>
              <label className="block space-y-1.5 text-sm">
                <span>Name</span>
                <input maxLength={120} className="w-full rounded border bg-transparent px-3 py-2" value={name} onChange={(event) => setName(event.target.value)} />
              </label>
              <label className="block space-y-1.5 text-sm">
                <span>Profile picture URL</span>
                <input type="url" placeholder="https://…" className="w-full rounded border bg-transparent px-3 py-2" value={picture} onChange={(event) => setPicture(event.target.value)} />
                <span className="text-xs text-muted-foreground">Use a public HTTPS image URL.</span>
              </label>
              <label className="block space-y-1.5 text-sm">
                <span>Phone number</span>
                <input type="tel" autoComplete="tel" maxLength={32} className="w-full rounded border bg-transparent px-3 py-2" value={phone} onChange={(event) => setPhone(event.target.value)} />
              </label>
              {error && <p role="alert" className="text-sm text-danger">{error}</p>}
              {notice && <p role="status" className="text-sm text-emerald-400">{notice}</p>}
              <button type="submit" disabled={busy || profile === null} className="rounded bg-primary px-4 py-2 text-sm font-medium text-primary-foreground disabled:opacity-50">{busy ? "Saving…" : "Save profile"}</button>
            </form>
          )}
          {error && profile === null && <p role="alert" className="text-sm text-danger">{error}</p>}
        </CardContent>
      </Card>
    </main>
  );
}
