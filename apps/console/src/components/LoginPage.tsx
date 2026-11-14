import { useAuth0 } from "@auth0/auth0-react";

export function LoginPage() {
  const { loginWithRedirect } = useAuth0();
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
          onClick={() => loginWithRedirect()}
        >
          Sign in
        </button>
        <button
          type="button"
          className="login-signup"
          onClick={() =>
            loginWithRedirect({
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
