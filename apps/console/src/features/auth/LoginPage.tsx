import { useAuth0 } from "@auth0/auth0-react";

export function LoginPage() {
  const { loginWithRedirect } = useAuth0();
  const inviteToken = new URLSearchParams(window.location.hash.slice(1)).get(
    "invite",
  );
  const continueTo = inviteToken
    ? `${window.location.pathname}#invite=${encodeURIComponent(inviteToken)}`
    : window.location.pathname !== "/"
      ? window.location.pathname + window.location.search
      : undefined;
  const signIn = (options?: Parameters<typeof loginWithRedirect>[0]) =>
    loginWithRedirect({
      ...options,
      ...(continueTo ? { appState: { returnTo: continueTo } } : {}),
    });
  return (
    <main className="login-screen">
      <div className="login-brand">
        <img src="/ringfour-logo.svg" alt="" />
        <span>ringfour</span>
      </div>
      <section className="login-panel">
        <h1>Welcome back</h1>
        <p>Sign in with your Ringfour account to continue.</p>
        <button
          type="button"
          className="button-primary"
          onClick={() => void signIn()}
        >
          Sign in
        </button>
        <button
          type="button"
          className="login-signup"
          onClick={() =>
            signIn({
              authorizationParams: { screen_hint: "signup" },
            })
          }
        >
          New to Ringfour? <strong>Sign up</strong>
        </button>
      </section>
    </main>
  );
}
