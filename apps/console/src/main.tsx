import { Auth0Provider } from "@auth0/auth0-react";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";

import "@fontsource/inter/400.css";
import "@fontsource/inter/500.css";
import "@fontsource/inter/600.css";
import "@fontsource/inter/700.css";
import App from "./App";
import "./index.css";

const domain = import.meta.env.VITE_AUTH0_DOMAIN as string | undefined;
const clientId = import.meta.env.VITE_AUTH0_CLIENT_ID as string | undefined;
const audience = import.meta.env.VITE_AUTH0_AUDIENCE as string | undefined;
// Keep the browser's origin for local and LAN access. Auth0 validates it
// against the application's registered callback and web origins.
const redirectUri = window.location.origin;

const rootElement = document.getElementById("root");
if (!rootElement) {
  throw new Error("Missing #root element");
}

createRoot(rootElement).render(
  <StrictMode>
    {domain && clientId ? (
      <Auth0Provider
        domain={domain}
        clientId={clientId}
        onRedirectCallback={(state) => {
          const target = new URL(
            state?.returnTo || "/overview",
            window.location.origin,
          );
          window.history.replaceState(
            null,
            "",
            target.origin === window.location.origin
              ? target.pathname + target.search + target.hash
              : "/overview",
          );
          window.dispatchEvent(new PopStateEvent("popstate"));
        }}
        cacheLocation="localstorage"
        authorizationParams={{
          redirect_uri: redirectUri,
          audience,
        }}
      >
        <App />
      </Auth0Provider>
    ) : (
      <main className="mx-auto max-w-2xl p-6">
        <p>
          Console is not configured: set VITE_AUTH0_DOMAIN and
          VITE_AUTH0_CLIENT_ID.
        </p>
      </main>
    )}
  </StrictMode>,
);
