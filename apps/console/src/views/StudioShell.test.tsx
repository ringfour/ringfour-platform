import {
  cleanup,
  fireEvent,
  render,
  screen,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test } from "vitest";
import { PageHeading } from "@/components/PageHeading";
import { StudioShell } from "./StudioShell";

afterEach(() => {
  cleanup();
  window.history.replaceState(null, "", "/");
});
test("sidebar owns main navigation and mobile navigation returns focus", async () => {
  const { container } = render(
    <StudioShell
      workspaces={[{ id: "org", name: "Workshop", role: "owner" }]}
      workspace="org"
      view="apps"
    >
      <h1>Apps</h1>
    </StudioShell>,
  );
  const navigation = screen.getByRole("navigation", {
    name: "Main navigation",
  });
  expect(
    screen.getByRole("navigation", { name: "Studio toolbar" }),
  ).toBeTruthy();
  expect(container.querySelector(".studio-sidebar")?.contains(navigation)).toBe(
    true,
  );
  for (const label of ["Home", "Apps", "Hub", "Robots", "Logbook"]) {
    expect(
      within(navigation)
        .getByRole("link", { name: label })
        .getAttribute("href"),
    ).toContain("workspace=org");
  }
  expect(
    within(navigation)
      .getByRole("link", { name: "Apps" })
      .getAttribute("aria-current"),
  ).toBe("page");
  const trigger = screen.getByRole("button", { name: "Open navigation" });
  await userEvent.click(trigger);
  const dialog = screen.getByRole("dialog", { name: "Navigation" });
  expect(within(dialog).getByRole("link", { name: "Apps" })).toBeTruthy();

  fireEvent(dialog, new Event("cancel", { cancelable: true }));
  expect(screen.queryByRole("dialog")).toBeNull();
  expect(document.activeElement).toBe(trigger);
});

test("page-heading back navigation preserves collection context with the top bar", () => {
  window.history.replaceState(
    { fromList: "/apps?workspace=org&robot=b" },
    "",
    "/apps/camera/settings?workspace=org&installation=install-b",
  );
  render(
    <StudioShell
      workspaces={[{ id: "org", name: "Workshop" }]}
      workspace="org"
      view="apps"
    >
      <main>
        <PageHeading title="Camera" />
      </main>
    </StudioShell>,
  );
  const main = screen.getByRole("main");
  expect(
    within(main)
      .getByRole("link", { name: "Back to Apps" })
      .getAttribute("href"),
  ).toBe("/apps?workspace=org&robot=b");
  expect(
    screen.getByRole("navigation", { name: "Studio toolbar" }),
  ).toBeTruthy();
});

test("account tools and organization dropdown share the restored top bar", () => {
  window.history.replaceState(null, "", "/settings/health?workspace=org");
  render(
    <StudioShell
      workspaces={[
        { id: "org", name: "Workshop", role: "owner" },
        { id: "other", name: "Other" },
      ]}
      workspace="org"
      view="health"
    >
      <main />
    </StudioShell>,
  );
  const account = screen.getByRole("navigation", { name: "Studio toolbar" });
  const rail = screen.getByRole("navigation", { name: "Main navigation" });
  expect(within(rail).getAllByRole("link")).toHaveLength(5);
  expect(account.closest(".studio-sidebar")).toBeNull();
  for (const name of ["Organizations", "System health", "My profile"]) {
    expect(within(rail).queryByRole("link", { name })).toBeNull();
    expect(
      within(account).getByRole("link", { name }).getAttribute("href"),
    ).toContain("workspace=org");
  }
  expect(
    within(account)
      .getByRole("link", { name: "System health" })
      .getAttribute("aria-current"),
  ).toBe("page");
  expect(
    (
      within(account).getByRole("combobox", {
        name: "Organization",
      }) as HTMLSelectElement
    ).value,
  ).toBe("org");
});

test("Timeline keeps Logbook selected in main navigation", () => {
  render(
    <StudioShell workspaces={[]} workspace="org" view="timeline">
      <main />
    </StudioShell>,
  );
  const navigation = screen.getByRole("navigation", {
    name: "Main navigation",
  });
  expect(
    within(navigation)
      .getByRole("link", { name: "Logbook" })
      .getAttribute("aria-current"),
  ).toBe("page");
});
