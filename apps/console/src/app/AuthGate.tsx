import { useAuth0 } from "@auth0/auth0-react";
import { type ReactNode, useEffect, useRef, useState } from "react";
import { LoginPage } from "@/features/auth/LoginPage";
import { AUTH_SESSION_EXPIRED_EVENT } from "@/lib/api";

/** Auth0 remains mandatory; expiry handling is independent of workspace routing. */
export function AuthGate({ children }: { children: ReactNode }) {
  const { isAuthenticated, isLoading, error, loginWithRedirect } = useAuth0();
  const [authWaitExpired, setAuthWaitExpired] = useState(false);
  const [authRedirecting, setAuthRedirecting] = useState(false);
  const [authRedirectError, setAuthRedirectError] = useState(false);
  const authRedirectStarted = useRef(false);
  useEffect(() => {
    if (!isLoading) {
      setAuthWaitExpired(false);
      return;
    }
    const timeout = window.setTimeout(() => setAuthWaitExpired(true), 10_000);
    return () => window.clearTimeout(timeout);
  }, [isLoading]);

  useEffect(() => {
    const handleExpiredSession = () => {
      if (!isAuthenticated || authRedirectStarted.current) return;
      authRedirectStarted.current = true;
      setAuthRedirecting(true);
      const returnTo = [
        window.location.pathname,
        window.location.search,
        window.location.hash,
      ].join("");
      void Promise.resolve(loginWithRedirect({ appState: { returnTo } })).catch(
        () => {
          authRedirectStarted.current = false;
          setAuthRedirecting(false);
          setAuthRedirectError(true);
        },
      );
    };
    window.addEventListener(AUTH_SESSION_EXPIRED_EVENT, handleExpiredSession);
    return () => {
      window.removeEventListener(
        AUTH_SESSION_EXPIRED_EVENT,
        handleExpiredSession,
      );
    };
  }, [isAuthenticated, loginWithRedirect]);

  if (authRedirecting)
    return (
      <main className="auth-wait">
        <p role="status">Your session expired. Redirecting to sign in…</p>
      </main>
    );
  if (authRedirectError)
    return (
      <main className="auth-wait">
        <p role="alert" className="text-danger">
          Your session expired. Sign in again to continue.
        </p>
        <button
          type="button"
          className="button-primary"
          onClick={() => void loginWithRedirect()}
        >
          Sign in
        </button>
      </main>
    );

  if (isLoading && authWaitExpired)
    return (
      <main className="auth-wait">
        <p role="alert" className="text-danger">
          Authentication is taking too long. Open Console at{" "}
          <a href={window.location.origin}>{window.location.origin}</a>.
        </p>
      </main>
    );
  if (isLoading)
    return <main className="auth-wait">Loading your workspace…</main>;
  if (error)
    return (
      <main className="auth-wait">
        <p role="alert" className="text-danger">
          Authentication error: {error.message}
        </p>
      </main>
    );
  if (!isAuthenticated) return <LoginPage />;

  return children;
}
