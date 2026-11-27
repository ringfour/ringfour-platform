import type { ReactNode } from "react";
import { StudioBackLink } from "@/views/StudioBackLink";

/** The Apps screen's header is the shared page chrome throughout Studio. */
export function PageHeading({
  title,
  description,
  metadata,
  actions,
  artwork,
  children,
  titleId,
  className = "",
}: {
  title: ReactNode;
  description?: ReactNode;
  metadata?: ReactNode;
  actions?: ReactNode;
  artwork?: ReactNode;
  children?: ReactNode;
  titleId?: string;
  className?: string;
}) {
  return (
    <header className={`page-heading studio-page-header ${className}`}>
      <StudioBackLink />
      <div className="studio-page-heading-row">
        <div className="studio-page-heading-copy">
          {metadata && (
            <div className="studio-page-heading-meta">{metadata}</div>
          )}
          <h1 id={titleId}>{title}</h1>
          {description && (
            <div className="studio-page-heading-description">{description}</div>
          )}
        </div>
        {artwork && (
          <div className="studio-page-heading-artwork">{artwork}</div>
        )}
        {actions && (
          <div className="studio-page-heading-actions">{actions}</div>
        )}
      </div>
      {children && <div className="studio-page-heading-footer">{children}</div>}
    </header>
  );
}
