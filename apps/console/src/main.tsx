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
const consoleOrigin = import.meta.env.VITE_CONSOLE_ORIGIN as string | undefined;
const redirectUri = consoleOrigin || window.location.origin;

// Auth0 SPA origins are exact. Canonicalize before the SDK starts so login,
// callback and silent authorization all use the registered local origin.
if (consoleOrigin && window.location.origin !== consoleOrigin) {
  const destination = new URL(
    `${window.location.pathname}${window.location.search}${window.location.hash}`,
    consoleOrigin,
  );
  window.location.replace(destination);
}

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
