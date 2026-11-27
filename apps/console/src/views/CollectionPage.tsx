import type { ReactNode } from "react";
import { StudioPage } from "./StudioPage";

/** Collection template: common chrome, connected filters and a results region. */
export function CollectionPage({
  heading,
  toolbar,
  filterLabel = "Filters",
  children,
}: {
  heading: ReactNode;
  toolbar?: ReactNode;
  filterLabel?: string;
  children: ReactNode;
}) {
  return (
    <StudioPage heading={heading}>
      <div className="studio-collection-body">
        {toolbar && (
          <section className="studio-filter-bar" aria-label={filterLabel}>
            {toolbar}
          </section>
        )}
        {children}
      </div>
    </StudioPage>
  );
}
