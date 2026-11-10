import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";

const getAccessTokenSilently = vi.fn(async () => "test-access-token");

vi.mock("@auth0/auth0-react", () => ({
  useAuth0: () => ({
    isAuthenticated: true,
    isLoading: false,
    getAccessTokenSilently,
  }),
}));

import { RobotsPage } from "./RobotsPage";

const ORG = {
  id: "9b8a7c6d-5e4f-4a3b-8c7d-6e5f4a3b2c1d",
  name: "Acme Robotics",
  role: "owner",
};

const ROBOT = {
  id: "7c2f9a1d-4e6b-4c8a-9d1e-2f3a4b5c6d7e",
  organization_id: ORG.id,
  name: "warehouse-01",
  created_at: "2026-11-09T08:20:11Z",
  enrolled_at: null,
  profile_ref: null,
};

const CREDENTIAL = {
  id: "3c4d5e6f-7a8b-4c9d-0e1f-2a3b4c5d6e7f",
  robot_id: ROBOT.id,
  created_at: "2026-11-09T08:22:40Z",
  expires_at: null,
  revoked_at: null,
};

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}

function stubApi(overrides?: {
  role?: string;
  robots?: unknown[];
  credentials?: unknown[];
  status?: unknown;
  onPost?: (url: string, body: unknown) => Response | undefined;
}) {
  const posts: { url: string; body: unknown }[] = [];
  const fetchSpy = vi.fn(
    async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (init?.method === "POST") {
        const body = init.body ? JSON.parse(String(init.body)) : null;
        posts.push({ url, body });
        const custom = overrides?.onPost?.(url, body);
        if (custom) {
          return custom;
        }
        return jsonResponse({}, 201);
      }
      if (url === "/api/v1/organizations") {
        return jsonResponse({
          items: [{ ...ORG, role: overrides?.role ?? ORG.role }],
          next_cursor: null,
        });
      }
      if (url.endsWith("/robots")) {
        return jsonResponse({
          items: overrides?.robots ?? [ROBOT],
          next_cursor: null,
        });
      }
      if (url.endsWith("/credentials")) {
        return jsonResponse({ items: overrides?.credentials ?? [CREDENTIAL] });
      }
      if (url.endsWith("/status")) {
        return jsonResponse(
          overrides?.status ?? {
            robot_id: ROBOT.id,
            online: true,
            last_seen_at: "2026-11-10T09:00:14Z",
            daemon_version: "0.1.0",
            protocol_version: 1,
          },
        );
      }
      if (url.includes("/robots/")) {
        return jsonResponse(ROBOT);
      }
      return jsonResponse(
        { error: { code: "not_found", message: "nope" } },
        404,
      );
    },
  );
  vi.stubGlobal("fetch", fetchSpy);
  return { fetchSpy, posts };
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

test("lists robots with enrollment status", async () => {
  stubApi();
  render(<RobotsPage />);

  expect(await screen.findByText("warehouse-01")).toBeTruthy();
  expect(screen.getByText("not enrolled")).toBeTruthy();
  // Organization picker defaults to the first org.
  expect(screen.getByLabelText(/organization/i)).toBeTruthy();
});

test("owner creates a robot", async () => {
  const { posts } = stubApi();
  render(<RobotsPage />);
  await screen.findByText("warehouse-01");

  await userEvent.type(
    screen.getByPlaceholderText(/new robot name/i),
    "lab-bench-02",
  );
  await userEvent.click(screen.getByRole("button", { name: /create/i }));

  expect(posts).toContainEqual({
    url: `/api/v1/organizations/${ORG.id}/robots`,
    body: { name: "lab-bench-02", profile_ref: null },
  });
});

test("owner issues an enrollment token shown exactly once", async () => {
  const { posts } = stubApi({
    onPost: (url) => {
      if (url.endsWith("/enrollment-tokens")) {
        return jsonResponse(
          {
            token_id: "2b3c4d5e-6f7a-4b8c-9d0e-1f2a3b4c5d6e",
            robot_id: ROBOT.id,
            token: "rft_9N2dKmX4pQvR7sT1wYzA3bC6dE8fG0hI1jK4lM5nO6p",
            created_at: "2026-11-09T08:21:30Z",
            expires_at: "2026-11-09T08:36:30Z",
          },
          201,
        );
      }
      return undefined;
    },
  });
  render(<RobotsPage />);

  await userEvent.click(await screen.findByText("warehouse-01"));
  await userEvent.click(
    await screen.findByRole("button", { name: /issue enrollment token/i }),
  );

  expect(posts).toContainEqual({
    url: `/api/v1/robots/${ROBOT.id}/enrollment-tokens`,
    body: null,
  });
  const secret = await screen.findByTestId("one-time-secret");
  expect(secret.textContent).toBe(
    "rft_9N2dKmX4pQvR7sT1wYzA3bC6dE8fG0hI1jK4lM5nO6p",
  );

  await userEvent.click(screen.getByRole("button", { name: /dismiss/i }));
  expect(screen.queryByTestId("one-time-secret")).toBeNull();
});

test("owner rotates and revokes credentials", async () => {
  const { posts } = stubApi({
    onPost: (url) => {
      if (url.endsWith("/credentials")) {
        return jsonResponse(
          {
            credential: {
              ...CREDENTIAL,
              id: "4d5e6f7a-8b9c-4d0e-1f2a-3b4c5d6e7f8a",
            },
            secret: "rfs_PqR7sT1wYzA3bC6dE8fG0hI1jK4lM5nO6p9N2dKmX4Q",
          },
          201,
        );
      }
      return jsonResponse({
        ...CREDENTIAL,
        revoked_at: "2026-11-09T09:30:00Z",
      });
    },
  });
  render(<RobotsPage />);

  await userEvent.click(await screen.findByText("warehouse-01"));
  await screen.findByText("Robot detail");

  await userEvent.click(
    screen.getByRole("button", { name: /rotate credential/i }),
  );
  expect(posts).toContainEqual({
    url: `/api/v1/robots/${ROBOT.id}/credentials`,
    body: null,
  });
  expect((await screen.findByTestId("one-time-secret")).textContent).toBe(
    "rfs_PqR7sT1wYzA3bC6dE8fG0hI1jK4lM5nO6p9N2dKmX4Q",
  );

  await userEvent.click(screen.getByRole("button", { name: /revoke/i }));
  expect(posts).toContainEqual({
    url: `/api/v1/robots/${ROBOT.id}/credentials/${CREDENTIAL.id}/revoke`,
    body: null,
  });
});

test("device status inspector shows observed presence", async () => {
  stubApi();
  render(<RobotsPage />);

  await userEvent.click(await screen.findByText("warehouse-01"));
  const panel = await screen.findByTestId("device-status");

  expect(panel.textContent).toContain("online");
  expect(panel.textContent).toContain("2026-11-10T09:00:14Z");
  expect(panel.textContent).toContain("0.1.0");
  expect(panel.textContent).toContain("v1");
});

test("device status inspector shows never-connected robots as offline", async () => {
  stubApi({
    status: {
      robot_id: ROBOT.id,
      online: false,
      last_seen_at: null,
      daemon_version: null,
      protocol_version: null,
    },
  });
  render(<RobotsPage />);

  await userEvent.click(await screen.findByText("warehouse-01"));
  const panel = await screen.findByTestId("device-status");

  expect(panel.textContent).toContain("offline");
  expect(panel.textContent).toContain("never");
});

test("non-owner members see a read-only view", async () => {
  stubApi({ role: "developer" });
  render(<RobotsPage />);

  await userEvent.click(await screen.findByText("warehouse-01"));
  await screen.findByText("Robot detail");

  expect(screen.queryByPlaceholderText(/new robot name/i)).toBeNull();
  expect(
    screen.queryByRole("button", { name: /issue enrollment token/i }),
  ).toBeNull();
  expect(
    screen.queryByRole("button", { name: /rotate credential/i }),
  ).toBeNull();
  expect(screen.queryByRole("button", { name: /revoke/i })).toBeNull();
});
