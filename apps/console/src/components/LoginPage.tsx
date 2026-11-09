import { useAuth0 } from "@auth0/auth0-react";

import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";

export function LoginPage() {
  const { loginWithRedirect } = useAuth0();

  return (
    <main className="mx-auto max-w-2xl p-6">
      <Card>
        <CardHeader>
          <CardTitle>Ringfour Console</CardTitle>
        </CardHeader>
        <CardContent>
          <p className="mb-4 text-muted-foreground">
            Sign in with your Ringfour account to continue.
          </p>
          <div className="flex gap-2">
            <button
              type="button"
              className="rounded-md bg-primary px-4 py-2 text-sm font-medium text-primary-foreground"
              onClick={() => loginWithRedirect()}
            >
              Sign in
            </button>
            <button
              type="button"
              className="rounded-md border px-4 py-2 text-sm font-medium"
              onClick={() =>
                loginWithRedirect({
                  authorizationParams: { screen_hint: "signup" },
                })
              }
            >
              Sign up
            </button>
          </div>
        </CardContent>
      </Card>
    </main>
  );
}
