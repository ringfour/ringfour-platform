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
  expect(loginWithRedirect).toHaveBeenCalledWith();
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

  expect(await screen.findByText("user-1")).toBeTruthy();
  expect(await screen.findByText("Acme Robotics")).toBeTruthy();
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
  await screen.findByText("Acme Robotics");

  await userEvent.type(
    screen.getByPlaceholderText(/new organization name/i),
    "Acme Robotics",
  );
  await userEvent.click(screen.getByRole("button", { name: /create/i }));
  expect(posts).toContain(JSON.stringify({ name: "Acme Robotics" }));

  await userEvent.click(
    await screen.findByRole("button", { name: "Acme Robotics" }),
  );
  expect(await screen.findByText("user-1", { selector: "td" })).toBeTruthy();
});
