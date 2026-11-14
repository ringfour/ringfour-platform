import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { strToU8, unzipSync, zipSync } from "fflate";
import { afterEach, expect, test, vi } from "vitest";

vi.mock("@auth0/auth0-react", () => ({
  useAuth0: () => ({ getAccessTokenSilently: token }),
}));
const token = vi.fn(async () => "token");

import { RecordsPage } from "./RecordsPage";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

test("opens mission revision and displays the selected source file", async () => {
  const archive = zipSync({
    "main.py": new Uint8Array(strToU8("print('hello')")),
    "requirements.lock": new Uint8Array(strToU8("# dependencies")),
  });
  expect(Object.keys(unzipSync(archive))).toContain("main.py");
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === "/api/v1/organizations")
        return json({
          items: [{ id: "org", name: "Acme" }],
          next_cursor: null,
        });
      if (url === "/api/v1/organizations/org/missions")
        return json({
          items: [
            {
              id: "mission",
              name: "hello",
              latest_version: 1,
              created_at: "today",
              created_by_user_id: "user",
            },
          ],
          next_cursor: null,
        });
      if (url === "/api/v1/organizations/org/robots")
        return json({ items: [], next_cursor: null });
      if (url === "/api/v1/missions/mission/revisions")
        return json({
          items: [
            {
              id: "rev",
              version: 1,
              created_at: "today",
              manifest: {
                entrypoint: "main.py",
                python_requirement: ">=3.13",
                source_digest: "abc",
                lock_file: "requirements.lock",
              },
            },
          ],
          next_cursor: null,
        });
      if (url === "/api/v1/missions/mission/revisions/rev/source")
        return new Response(new Uint8Array(archive).buffer);
      return json({ error: { message: "missing" } }, 404);
    }),
  );
  render(
    <RecordsPage
      kind="missions"
      preferredOrganizationId="org"
      onOrganizationChange={() => {}}
    />,
  );
  await userEvent.click(
    await screen.findByRole("button", { name: /hello version 1/i }),
  );
  expect(await screen.findByText("print('hello')")).toBeTruthy();
  await userEvent.selectOptions(
    screen.getByLabelText("File"),
    "requirements.lock",
  );
  expect(screen.getByText("# dependencies")).toBeTruthy();
});

test("lists deployments and exposes state and failures", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === "/api/v1/organizations")
        return json({
          items: [{ id: "org", name: "Acme" }],
          next_cursor: null,
        });
      if (url === "/api/v1/organizations/org/missions")
        return json({ items: [], next_cursor: null });
      if (url === "/api/v1/organizations/org/robots")
        return json({
          items: [{ id: "robot", name: "base101" }],
          next_cursor: null,
        });
      if (url === "/api/v1/organizations/org/deployments")
        return json({
          items: [
            {
              id: "deployment",
              robot_id: "robot",
              desired_state: "stopped",
              observed_state: "failed",
              last_error: { code: "failed", message: "Preparation failed" },
              created_at: "today",
              updated_at: "today",
            },
          ],
          next_cursor: null,
        });
      return json({ error: { message: "missing" } }, 404);
    }),
  );
  render(
    <RecordsPage
      kind="deployments"
      preferredOrganizationId="org"
      onOrganizationChange={() => {}}
    />,
  );
  await userEvent.click(
    await screen.findByRole("button", { name: /base101 failed/i }),
  );
  expect(screen.getByText("failed: Preparation failed")).toBeTruthy();
  expect(screen.getByText("deployment")).toBeTruthy();
});
