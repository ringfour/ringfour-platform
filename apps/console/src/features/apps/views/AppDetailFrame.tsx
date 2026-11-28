// Shared header/tabs adapted from Lovable src/routes/apps.$appId.tsx (1b99764).

import { ChevronDown } from "lucide-react";
import type { ReactNode } from "react";
import { PageHeading } from "@/components/PageHeading";
import { Publisher } from "@/features/apps/views/Publisher";
import type { App, Installation, Revision, Robot } from "@/lib/apps";
import { appPath, robotCredentialsPath, visit } from "@/lib/location";
import { StudioPage } from "@/views/StudioPage";
import { StudioTabs } from "@/views/StudioTabs";
import { StoreHeaderImage } from "./StoreHeaderImage";
export function AppDetailFrame({
  app,
  workspace,
  installations,
  robots,
  selected,
  section,
  revision,
  operation,
  execution,
  children,
}: {
  app: App;
  workspace: string;
  installations: Installation[];
  robots: Robot[];
  selected?: Installation;
  section: string;
  revision?: Revision;
  operation?: string;
  execution?: ReactNode;
  children: ReactNode;
}) {
  const robot = robots.find((item) => item.id === selected?.robot_id);
  return (
    <StudioPage
      heading={
        <PageHeading
          className="studio-resource-header"
          title={app.name}
          titleId="app-overview-title"
          description={revision?.manifest.display.description}
          metadata={
            <>
              <Publisher app={app} workspace={workspace} />
              {revision ? ` · Release ${revision.manifest.release}` : ""}
            </>
          }
          actions={
            <div className="studio-app-target">
              <label
                htmlFor="app-robot-select"
                className="text-xs font-medium text-muted-foreground"
              >
                Robot
              </label>
              <div className="relative">
                <select
                  id="app-robot-select"
                  value={selected?.id ?? ""}
                  onChange={(event) => {
                    const url = new URL(
                      appPath(
                        app.id,
                        workspace,
                        event.target.value || undefined,
                        section,
                      ),
                      window.location.origin,
                    );
                    if (!visit(url.pathname + url.search))
                      event.currentTarget.value = selected?.id ?? "";
                  }}
                  className="studio-field appearance-none py-1.5 pl-3 pr-8 text-sm font-medium text-foreground"
                >
                  <option value="">Select a robot</option>
                  {installations.map((item) => (
                    <option key={item.id} value={item.id} className="bg-card">
                      {robots.find((robot) => robot.id === item.robot_id)
                        ?.name ?? "Robot"}
                      {item.removed_at
                        ? " · Removed"
                        : item.installed
                          ? ""
                          : " · Installing"}
                    </option>
                  ))}
                </select>
                <ChevronDown className="pointer-events-none absolute right-2.5 top-1/2 h-3.5 w-3.5 text-muted-foreground -translate-y-1/2" />
              </div>
              {robot && (
                <a
                  href={robotCredentialsPath(robot.id, workspace)}
                  className="sr-only"
                >
                  Device credentials for {robot.name}
                </a>
              )}
            </div>
          }
        >
          <div className="studio-app-header-content">
            <StoreHeaderImage app={app} priority />
            {execution}
          </div>
        </PageHeading>
      }
    >
      <StudioTabs label="App sections">
        {["overview", "settings", "files", "activity"].map((tab) => (
          <a
            key={tab}
            href={
              appPath(app.id, workspace, selected?.id, tab) +
              (operation ? `&operation=${encodeURIComponent(operation)}` : "")
            }
            aria-current={section === tab ? "page" : undefined}
          >
            {tab[0].toUpperCase() + tab.slice(1)}
          </a>
        ))}
      </StudioTabs>
      {children}
    </StudioPage>
  );
}
