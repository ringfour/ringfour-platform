// @vitest-environment-options {"url":"http://192.168.1.133:5173/robots/virt2?workspace=org"}
import type { ReactElement } from "react";
import { afterEach, expect, test, vi } from "vitest";

const { render } = vi.hoisted(() => ({ render: vi.fn() }));
vi.mock("react-dom/client", () => ({ createRoot: () => ({ render }) }));
vi.mock("@auth0/auth0-react", () => ({ Auth0Provider: () => null }));
vi.mock("./App", () => ({ default: () => null }));

afterEach(() => {
  vi.unstubAllEnvs();
});

test("LAN bootstrap and Auth0 callback stay on the visiting origin despite a legacy localhost setting", async () => {
  vi.stubEnv("VITE_AUTH0_DOMAIN", "tenant.example");
  vi.stubEnv("VITE_AUTH0_CLIENT_ID", "public-client");
  vi.stubEnv("VITE_CONSOLE_ORIGIN", "http://localhost:5173");
  document.body.innerHTML = '<div id="root"></div>';
  await import("./main");
  const root = render.mock.calls[0][0] as ReactElement<{
    children: ReactElement<{
      authorizationParams: { redirect_uri: string };
      onRedirectCallback: (state: { returnTo: string }) => void;
    }>;
  }>;
  const provider = root.props.children;
  expect(provider.props.authorizationParams.redirect_uri).toBe(
    "http://192.168.1.133:5173",
  );
  expect(window.location.href).toBe(
    "http://192.168.1.133:5173/robots/virt2?workspace=org",
  );
  provider.props.onRedirectCallback({ returnTo: "/apps?workspace=org" });
  expect(window.location.href).toBe(
    "http://192.168.1.133:5173/apps?workspace=org",
  );
  provider.props.onRedirectCallback({ returnTo: "https://external.example/" });
  expect(window.location.href).toBe("http://192.168.1.133:5173/overview");
});
