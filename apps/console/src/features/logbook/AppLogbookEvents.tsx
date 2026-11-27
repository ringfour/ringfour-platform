import { StudioTime } from "@/components/StudioTime";
import { eventTitle, eventTone } from "@/lib/app-history";
import type { AppLogbookEvent } from "@/lib/apps";

/** Presentation restored from LogbookPage.tsx at 4204161; current App events only. */
export function AppLogbookEvents({
  events,
  error,
  loading,
  person,
}: {
  events: AppLogbookEvent[];
  error: string;
  loading: boolean;
  person: (id: string | null) => string | undefined;
}) {
  return (
    <section className="logbook2-section" aria-label="Logbook timeline">
      <header className="logbook2-section-heading">
        <div>
          <span className="logbook2-section-index">01</span>
          <h2>Timeline</h2>
        </div>
        <span>{events.length} events</span>
      </header>
      {error && (
        <p role="alert" className="logbook2-detail-error">
          Timeline unavailable: {error}
          {events.length > 0 && " · Showing the last received events."}
        </p>
      )}
      {loading ? (
        <p className="logbook2-empty">Loading events…</p>
      ) : (
        <div className="logbook2-timeline">
          {!events.length && !error && (
            <p className="logbook2-empty">No events recorded.</p>
          )}
          {events.map((event) => {
            const selectedLabel = event.payload.selected_label;
            return (
              <details
                className="logbook2-event"
                data-tone={eventTone(event)}
                key={event.event_id}
              >
                <summary>
                  <span className="logbook2-event-marker" />
                  <span className="logbook2-event-copy">
                    <strong>{eventTitle(event)}</strong>
                    <small>
                      {event.type}
                      {typeof selectedLabel === "string" &&
                        ` · Selected: ${selectedLabel}`}
                    </small>
                  </span>
                  <StudioTime value={event.occurred_at} />
                  <span className="logbook2-event-open" aria-hidden="true">
                    +
                  </span>
                </summary>
                <div className="logbook2-event-detail">
                  <dl>
                    {person(event.actor_user_id) && (
                      <>
                        <dt>Actor</dt>
                        <dd>{person(event.actor_user_id)}</dd>
                      </>
                    )}
                    <dt>Received</dt>
                    <dd>
                      <StudioTime value={event.received_at} />
                    </dd>
                    <dt>Operation</dt>
                    <dd>{event.operation_id ?? "—"}</dd>
                  </dl>
                  {typeof selectedLabel === "string" && (
                    <section
                      className="logbook2-hitl-decision"
                      aria-label="Selected options"
                    >
                      <header>Selected option</header>
                      <div className="logbook2-hitl-selections">
                        <div>
                          <strong>{selectedLabel}</strong>
                          {typeof event.payload.outcome === "string" && (
                            <code>{event.payload.outcome}</code>
                          )}
                        </div>
                      </div>
                    </section>
                  )}
                  <details>
                    <summary>Technical details</summary>
                    <p>
                      Event ID · <code>{event.event_id}</code>
                    </p>
                    <pre>{JSON.stringify(event.payload, null, 2)}</pre>
                  </details>
                </div>
              </details>
            );
          })}
        </div>
      )}
    </section>
  );
}
