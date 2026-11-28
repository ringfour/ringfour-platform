import { BookText, Bot, Grid2x2, Home, Library, Menu, X } from "lucide-react";
import { type ReactNode, useEffect, useRef, useState } from "react";
import { type StudioView, studioPath } from "@/lib/location";
import { StudioWorkspace } from "./StudioBackLink";
import { StudioTopBar } from "./StudioTopBar";

const NAV_ITEMS = [
  { label: "Home", view: "overview", icon: Home },
  { label: "Apps", view: "apps", icon: Grid2x2 },
  { label: "Hub", view: "store", icon: Library },
  { label: "Robots", view: "robots", icon: Bot },
  { label: "Logbook", view: "logbook", icon: BookText },
] as const;
export function StudioShell({
  children,
  workspaces,
  workspace,
  view,
  onWorkspaceChange,
}: {
  children: ReactNode;
  workspaces: { id: string; name: string; role?: string }[];
  workspace: string;
  view: StudioView | "unavailable";
  onWorkspaceChange?: (id: string) => void;
}) {
  const [mobileOpen, setMobileOpen] = useState(false);
  const drawer = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    if (!mobileOpen) return;
    const node = drawer.current;
    const trigger = document.activeElement as HTMLElement | null;
    if (node && !node.open) {
      if (typeof node.showModal === "function") node.showModal();
      else node.setAttribute("open", "");
    }
    const viewport = window.matchMedia?.("(min-width: 1024px)");
    const closeOnDesktop = () => {
      if (viewport?.matches) setMobileOpen(false);
    };
    viewport?.addEventListener("change", closeOnDesktop);
    return () => {
      viewport?.removeEventListener("change", closeOnDesktop);
      if (node?.open && typeof node.close === "function") node.close();
      trigger?.focus();
    };
  }, [mobileOpen]);
  const sidebarBody = (mobile = false) => (
    <div className="studio-sidebar-body flex h-full flex-col bg-sidebar text-sidebar-foreground">
      <div className="studio-rail-brand border-b border-sidebar-border">
        <a
          href={studioPath("overview", workspace)}
          className="studio-brand"
          aria-label="Ringfour Studio home"
          title="Ringfour Studio"
          onClick={() => setMobileOpen(false)}
        >
          <span className="sr-only">Ringfour Studio home</span>
          <span className="studio-brand-mark" aria-hidden="true" />
        </a>
      </div>
      <nav aria-label="Main navigation" className="studio-rail-nav flex-1">
        {NAV_ITEMS.map(({ label, view: target, icon: Icon }) => (
          <a
            key={target}
            href={studioPath(target, workspace)}
            aria-label={label}
            aria-current={
              view === target || (target === "logbook" && view === "timeline")
                ? "page"
                : undefined
            }
            onClick={() => setMobileOpen(false)}
          >
            <Icon size={22} strokeWidth={1.75} aria-hidden="true" />
            <span className="studio-rail-tooltip" aria-hidden="true">
              {label}
            </span>
          </a>
        ))}
      </nav>
      {mobile && (
        <button
          type="button"
          className="studio-rail-close"
          onClick={() => setMobileOpen(false)}
          aria-label="Close navigation"
        >
          <X size={20} aria-hidden="true" />
        </button>
      )}
    </div>
  );
  return (
    <div className="studio-shell flex min-h-screen w-full bg-background text-foreground">
      <aside className="studio-sidebar hidden w-[72px] shrink-0 border-r border-sidebar-border lg:block">
        <div className="sticky top-0 h-screen">{sidebarBody()}</div>
      </aside>
      {mobileOpen && (
        <dialog
          ref={drawer}
          aria-label="Navigation"
          className="studio-mobile-navigation"
          onCancel={(event) => {
            event.preventDefault();
            setMobileOpen(false);
          }}
        >
          <aside className="studio-mobile-rail w-[72px] border-r border-sidebar-border shadow-xl">
            {sidebarBody(true)}
          </aside>
          <button
            type="button"
            className="flex-1 bg-background/70"
            tabIndex={-1}
            onClick={() => setMobileOpen(false)}
            aria-label="Close navigation backdrop"
          />
        </dialog>
      )}
      <div className="studio-shell-content flex min-w-0 flex-1 flex-col">
        <header className="studio-mobile-header sticky top-0 z-30 flex items-center justify-between gap-3 border-b border-border bg-sidebar px-4 py-2.5 lg:hidden">
          <div className="flex min-w-0 items-center gap-2.5">
            <button
              type="button"
              onClick={() => setMobileOpen(true)}
              className="rounded-[6px] border border-border p-1.5 text-foreground hover:bg-accent"
              aria-label="Open navigation"
            >
              <Menu className="h-4 w-4" />
            </button>
            <a
              href={studioPath("overview", workspace)}
              className="flex items-center gap-2 font-display text-sm font-bold text-foreground"
            >
              <span className="studio-mobile-brand" aria-hidden="true">
                <span className="studio-brand-mark" />
              </span>
              <span>Ringfour Studio</span>
            </a>
          </div>
        </header>
        <StudioTopBar
          workspace={workspace}
          workspaces={workspaces}
          view={view}
          onWorkspaceChange={onWorkspaceChange}
        />
        <div className="studio-page-content min-w-0 flex-1">
          <StudioWorkspace.Provider value={workspace}>
            {children}
          </StudioWorkspace.Provider>
        </div>
      </div>
    </div>
  );
}
