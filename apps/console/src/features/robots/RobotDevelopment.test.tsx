import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";
import type { Installation } from "@/lib/apps";
import { RobotDevelopment } from "./RobotDevelopment";

const token = async () => "token";
const installation = {
  id: "installation",
  app_id: "app",
  installed: { revision_id: "revision" },
} as Installation;
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

test("enables an idle robot for one installed App and offers CLI connection steps", async () => {
  let enabled = false;
  const calls: RequestInit[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (_url, init?: RequestInit) => {
      if (init?.method === "POST") {
        calls.push(init);
        enabled = true;
        return Response.json({
          session_id: "session",
          installation_id: "installation",
          generation: 0,
          expires_at: "2099-01-01T00:00:00Z",
        });
      }
      return Response.json({ session: null });
    }),
  );
  render(
    <RobotDevelopment
      robotId="robot"
      token={token}
      installations={[installation]}
      apps={{}}
      activeRun={false}
      onChange={() => {}}
    />,
  );
  const button = await screen.findByRole("button", {
    name: "Enable development mode",
  });
  await vi.waitFor(() =>
    expect((button as HTMLButtonElement).disabled).toBe(false),
  );
  await userEvent.click(button);
  expect(enabled).toBe(true);
  expect(JSON.parse(calls[0].body as string)).toEqual({
    installation_id: "installation",
    ttl_seconds: 3600,
  });
  expect(
    await screen.findByText(/ringfour-cli app dev connect --robot robot/),
  ).toBeTruthy();
});

test("cannot enable development mode over an active App", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn(async () => Response.json({ session: null })),
  );
  render(
    <RobotDevelopment
      robotId="robot"
      token={token}
      installations={[installation]}
      apps={{}}
      activeRun
      onChange={() => {}}
    />,
  );
  expect(
    (
      (await screen.findByRole("button", {
        name: "Enable development mode",
      })) as HTMLButtonElement
    ).disabled,
  ).toBe(true);
  expect(screen.getByText(/Stop the current App/)).toBeTruthy();
});
