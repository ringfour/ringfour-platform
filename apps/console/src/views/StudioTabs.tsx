import type { ReactNode } from "react";

/** Feature navigation stays with its page content. */
export function StudioTabs({
  label,
  children,
}: {
  label: string;
  children: ReactNode;
}) {
  return (
    <nav className="studio-page-tabs" aria-label={label}>
      {children}
    </nav>
  );
}
