import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";

const loginWithRedirect = vi.fn();
const getAccessTokenSilently = vi.fn(async () => "test-access-token");
const logout = vi.fn();

type AuthState = {
  isAuthenticated: boolean;
  isLoading: boolean;
};

let authState: AuthState = { isAuthenticated: false, isLoading: false };

vi.mock("@auth0/auth0-react", () => ({
  useAuth0: () => ({
    ...authState,
    loginWithRedirect,
    getAccessTokenSilently,
    logout,
  }),
}));

import App from "./App";

afterEach(() => {
  cleanup();
  window.history.replaceState(null, "", "/");
  vi.unstubAllGlobals();
  authState = { isAuthenticated: false, isLoading: false };
  loginWithRedirect.mockClear();
  getAccessTokenSilently.mockClear();
});

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}

test("denies unauthenticated access and offers sign-in", async () => {
  const fetchSpy = vi.fn();
  vi.stubGlobal("fetch", fetchSpy);

  render(<App />);

  expect(
    await screen.findByText(/sign in with your ringfour account/i),
  ).toBeTruthy();
  expect(fetchSpy).not.toHaveBeenCalled();

  await userEvent.click(screen.getByRole("button", { name: /^sign in$/i }));
  expect(loginWithRedirect).toHaveBeenCalledWith({});
});

test("sign-up opens the Auth0 signup screen", async () => {
  vi.stubGlobal("fetch", vi.fn());

  render(<App />);

  await userEvent.click(
    await screen.findByRole("button", { name: /sign up/i }),
  );
  expect(loginWithRedirect).toHaveBeenCalledWith({
    authorizationParams: { screen_hint: "signup" },
  });
});

test("loads identity and organizations when authenticated", async () => {
  authState = { isAuthenticated: true, isLoading: false };
  const fetchSpy = vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input);
    if (url === "/api/v1/me") {
      return jsonResponse({
        user: { id: "user-1", created_at: "2026-11-09T08:15:30Z" },
        memberships: [],
      });
    }
    if (url === "/api/v1/organizations") {
      return jsonResponse({
        items: [
          {
            id: "9b8a7c6d-5e4f-4a3b-8c7d-6e5f4a3b2c1d",
            name: "Acme Robotics",
            role: "owner",
            created_at: "2026-11-09T08:16:02Z",
          },
        ],
        next_cursor: null,
      });
    }
    return jsonResponse({ error: { code: "not_found", message: "nope" } }, 404);
  });
  vi.stubGlobal("fetch", fetchSpy);

  render(<App />);
  await userEvent.click(screen.getByRole("link", { name: "Organizations" }));

  expect(await screen.findByText("user-1")).toBeTruthy();
  expect(
    await screen.findByText("Acme Robotics", { selector: "button span" }),
  ).toBeTruthy();
  expect(getAccessTokenSilently).toHaveBeenCalled();
  const [, init] = fetchSpy.mock.calls[0] as unknown as [string, RequestInit];
  expect((init.headers as Record<string, string>).Authorization).toBe(
    "Bearer test-access-token",
  );
});

test("creates an organization and inspects membership", async () => {
  authState = { isAuthenticated: true, isLoading: false };
  const posts: string[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (init?.method === "POST") {
        posts.push(String(init.body));
        return jsonResponse(
          {
            id: "9b8a7c6d-5e4f-4a3b-8c7d-6e5f4a3b2c1d",
            name: "Acme Robotics",
            created_at: "2026-11-09T08:16:02Z",
          },
          201,
        );
      }
      if (url === "/api/v1/me") {
        return jsonResponse({
          user: { id: "user-1", created_at: "2026-11-09T08:15:30Z" },
          memberships: [],
        });
      }
      if (url === "/api/v1/organizations") {
        return jsonResponse({
          items: [
            {
              id: "9b8a7c6d-5e4f-4a3b-8c7d-6e5f4a3b2c1d",
              name: "Acme Robotics",
              role: "owner",
              created_at: "2026-11-09T08:16:02Z",
            },
          ],
          next_cursor: null,
        });
      }
      if (url.includes("/members")) {
        return jsonResponse({
          items: [
            {
              organization_id: "9b8a7c6d-5e4f-4a3b-8c7d-6e5f4a3b2c1d",
              user_id: "user-1",
              role: "owner",
              created_at: "2026-11-09T08:16:02Z",
            },
          ],
          next_cursor: null,
        });
      }
      return jsonResponse({}, 404);
    }),
  );

  render(<App />);
  await userEvent.click(screen.getByRole("link", { name: "Organizations" }));
  await screen.findByText("Acme Robotics", { selector: "button span" });

  await userEvent.type(
    screen.getByPlaceholderText(/new organization name/i),
    "Acme Robotics",
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Create organization" }),
  );
  expect(posts).toContain(JSON.stringify({ name: "Acme Robotics" }));

  await userEvent.click(
    await screen.findByRole("button", { name: /^Acme Robotics/ }),
  );
  expect(
    await screen.findByText("user-1", { selector: "td span" }),
  ).toBeTruthy();
});

test("opens an App run from a deep link and follows its robot link", async () => {
  window.history.replaceState(null, "", "/runs/run-1?workspace=org&tab=Output");
  authState = { isAuthenticated: true, isLoading: false };
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), "http://localhost");
      const run = {
        id: "run-1",
        organization_id: "org",
        robot_id: "robot",
        installation_id: "installation",
        start_operation_id: "operation",
        snapshot: {
          app_id: "app",
          revision_id: "revision",
          publisher_organization_id: "publisher",
          configuration_revision_id: "configuration",
          configuration: {},
          image: "registry/app@sha256:abc",
        },
        state: "succeeded",
        requested_by: "operator",
        requested_at: "2026-11-20T08:00:00Z",
        started_at: "2026-11-20T08:00:01Z",
        ended_at: "2026-11-20T08:00:02Z",
        completed_at: "2026-11-20T08:00:02Z",
        failure: null,
      };
      const items: Record<string, unknown[]> = {
        "/api/v1/organizations": [
          { id: "org", name: "Workshop", role: "owner" },
        ],
        "/api/v1/organizations/org/robots": [
          { id: "robot", organization_id: "org", name: "Inspection arm" },
        ],
        "/api/v1/organizations/org/app-runs": [run],
        "/api/v1/organizations/org/app-logbook": [],
      };
      if (url.pathname.endsWith("/me"))
        return jsonResponse({ user: { id: "operator" } });
      if (url.pathname === "/api/v1/apps/app")
        return jsonResponse({
          id: "app",
          name: "Robot Check",
          description: "IMU",
        });
      if (url.pathname === "/api/v1/app-runs/run-1") return jsonResponse(run);
      if (url.pathname.endsWith("/artifacts"))
        return jsonResponse({ items: [] });
      if (url.pathname.endsWith("/output"))
        return jsonResponse({
          data_base64: btoa("sample output"),
          next_offset: 13,
          sealed: true,
          truncated: false,
        });
      return jsonResponse({
        items: items[url.pathname] ?? [],
        next_cursor: null,
      });
    }),
  );
  render(<App />);
  expect(
    await screen.findByRole("heading", { name: "Robot Check" }),
  ).toBeTruthy();
  expect(
    screen.getByRole("link", { name: "Inspection arm" }).getAttribute("href"),
  ).toBe("/robots/robot?workspace=org");
  expect((await screen.findAllByText(/sample output/)).length).toBe(2);
});

test("organization dropdown respects cancelled navigation and switches to the target collection", async () => {
  authState = { isAuthenticated: true, isLoading: false };
  window.history.replaceState(null, "", "/robots/robot-a?workspace=org");
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input), "http://localhost").pathname;
      if (path === "/api/v1/organizations")
        return jsonResponse({
          items: [
            { id: "org", name: "Current organization", role: "owner" },
            { id: "other", name: "Other organization", role: "operator" },
          ],
          next_cursor: null,
        });
      if (path === "/api/v1/robots/robot-a")
        return jsonResponse({
          id: "robot-a",
          organization_id: "org",
          name: "Robot A",
        });
      return jsonResponse({ items: [], next_cursor: null });
    }),
  );
  render(<App />);
  await screen.findByRole("option", { name: "Other organization" });
  const picker = screen.getByRole("combobox", {
    name: "Organization",
  }) as HTMLSelectElement;
  const cancel = (event: Event) => event.preventDefault();
  window.addEventListener("ringfour:navigation", cancel);
  try {
    await userEvent.selectOptions(picker, "other");
    expect(window.location.pathname).toBe("/robots/robot-a");
    expect(new URLSearchParams(window.location.search).get("workspace")).toBe(
      "org",
    );
    expect(picker.value).toBe("org");
  } finally {
    window.removeEventListener("ringfour:navigation", cancel);
  }
  await userEvent.selectOptions(picker, "other");
  expect(window.location.pathname).toBe("/robots");
  expect(new URLSearchParams(window.location.search).get("workspace")).toBe(
    "other",
  );
  expect(picker.value).toBe("other");
  expect(await screen.findByRole("heading", { name: "Robots" })).toBeTruthy();
});
