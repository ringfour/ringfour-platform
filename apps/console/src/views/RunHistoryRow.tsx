import { ChevronDown } from "lucide-react";
import { type ReactNode, useState } from "react";
import { StudioTime } from "@/components/StudioTime";
import { RunStatus } from "./RunStatus";

/** Activity, Logbook and device history share outcome/time/action columns. */
export function RunHistoryRow({
  title,
  detail,
  state,
  time,
  href,
  linkLabel = "Open run",
  openOnRow = false,
  eventCount,
  partial = false,
  children,
}: {
  title: ReactNode;
  detail?: ReactNode;
  state?: string;
  time: string;
  href?: string;
  linkLabel?: string;
  openOnRow?: boolean;
  eventCount?: number;
  partial?: boolean;
  children?: ReactNode;
}) {
  const [expanded, setExpanded] = useState(false);
  return (
    <article className="studio-history-record">
      <div
        className="studio-history-row"
        data-open-on-row={openOnRow || undefined}
      >
        <div className="studio-history-identity">
          <div className="studio-history-title">
            {openOnRow && href ? <a href={href}>{title}</a> : title}
          </div>
          {detail && <div className="studio-row-detail">{detail}</div>}
        </div>
        <div className="studio-history-state">
          {state ? (
            <RunStatus state={state} />
          ) : (
            <span className="text-muted-foreground">Installation activity</span>
          )}
        </div>
        <div className="studio-row-time">
          <StudioTime value={time} />
        </div>
        <div className="studio-history-count">
          {eventCount !== undefined && (
            <button
              type="button"
              aria-expanded={expanded}
              onClick={() => setExpanded((value) => !value)}
            >
              <ChevronDown size={16} aria-hidden="true" />
              {eventCount}{" "}
              {partial
                ? "loaded events"
                : eventCount === 1
                  ? "event"
                  : "events"}
            </button>
          )}
        </div>
        <div className="studio-row-action">
          {href &&
            (openOnRow ? (
              <span aria-hidden="true">Open run →</span>
            ) : (
              <a href={href} aria-label={linkLabel}>
                Open run →
              </a>
            ))}
        </div>
      </div>
      {expanded && children && (
        <div className="studio-row-events">{children}</div>
      )}
    </article>
  );
}
