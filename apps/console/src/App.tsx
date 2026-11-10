import { useAuth0 } from "@auth0/auth0-react";
import { useState } from "react";

import { HealthPage } from "@/components/HealthPage";
import { IdentityPage } from "@/components/IdentityPage";
import { LoginPage } from "@/components/LoginPage";
import { RobotsPage } from "@/components/RobotsPage";

export default function App() {
  if (!import.meta.env.DEV) {
    return (
      <main className="mx-auto max-w-2xl p-6">
        <p>Ringfour Console is a development-only tool.</p>
      </main>
    );
  }
  return <DevConsole />;
}

function DevConsole() {
  const { isAuthenticated, isLoading, error } = useAuth0();
  const [view, setView] = useState<"identity" | "robots" | "health">(
    "identity",
  );

  if (isLoading) {
    return (
      <main className="mx-auto max-w-2xl p-6">
        <p className="text-muted-foreground">Loading…</p>
      </main>
    );
  }
  if (error) {
    return (
      <main className="mx-auto max-w-2xl p-6">
        <p role="alert" className="text-sm text-destructive">
          Authentication error: {error.message}
        </p>
      </main>
    );
  }
  if (!isAuthenticated) {
    return <LoginPage />;
  }

  return (
    <>
      <nav className="mx-auto flex max-w-2xl gap-4 px-6 pt-6 text-sm">
        <button
          type="button"
          className={
            view === "identity"
              ? "font-medium text-primary"
              : "text-muted-foreground"
          }
          onClick={() => setView("identity")}
        >
          Identity
        </button>
        <button
          type="button"
          className={
            view === "robots"
              ? "font-medium text-primary"
              : "text-muted-foreground"
          }
          onClick={() => setView("robots")}
        >
          Robots
        </button>
        <button
          type="button"
          className={
            view === "health"
              ? "font-medium text-primary"
              : "text-muted-foreground"
          }
          onClick={() => setView("health")}
        >
          Health
        </button>
      </nav>
      {view === "identity" && <IdentityPage />}
      {view === "robots" && <RobotsPage />}
      {view === "health" && <HealthPage />}
    </>
  );
}
