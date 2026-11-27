import type { ReactNode } from "react";

/** Shared page template: full-width chrome and a left-anchored 12-column body. */
export function StudioPage({
  heading,
  children,
  width = "full",
  className = "",
}: {
  heading: ReactNode;
  children?: ReactNode;
  width?: "full" | "reading";
  className?: string;
}) {
  return (
    <main className={`studio-container studio-page ${className}`}>
      {heading}
      <div className="studio-content-grid" data-width={width}>
        {children}
      </div>
    </main>
  );
}

/** Section cards share padding and heading/action alignment; forms share one width. */
export function StudioSection({
  title,
  titleId,
  description,
  actions,
  children,
  width = "full",
  className = "",
}: {
  title: ReactNode;
  titleId: string;
  description?: ReactNode;
  actions?: ReactNode;
  children?: ReactNode;
  width?: "full" | "form";
  className?: string;
}) {
  return (
    <section
      className={`studio-section ${className}`}
      data-width={width}
      aria-labelledby={titleId}
    >
      <header className="studio-section-heading">
        <div>
          <h2 id={titleId}>{title}</h2>
          {description && (
            <div className="studio-section-description">{description}</div>
          )}
        </div>
        {actions && <div className="studio-section-actions">{actions}</div>}
      </header>
      {children && <div className="studio-section-body">{children}</div>}
    </section>
  );
}
