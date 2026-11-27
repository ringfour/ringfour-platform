import { useEffect, useRef, useState } from "react";
import { apiFetch, apiFetchAll } from "@/lib/api";
import { resourcePath } from "@/lib/location";

type InteractionChoice = { value: string; label: string };
type InteractionResponse = {
  response_id: string;
  chosen_value: string;
  status: "submitted" | "applied" | "rejected_timed_out" | "rejected_cancelled";
};
export type Interaction = {
  created_at: string;
  request_id: string;
  robot_id: string;
  execution_id: string;
  prompt: string;
  choices: InteractionChoice[];
  deadline: string | null;
  state: "pending" | "answered" | "timed_out" | "cancelled";
  response: InteractionResponse | null;
};
function deadlineLabel(value: string | null) {
  if (value === null) return "No timeout";
  const milliseconds = new Date(value).getTime() - Date.now();
  if (!Number.isFinite(milliseconds)) return value;
  if (milliseconds <= 0) return "expired";
  const minutes = Math.ceil(milliseconds / 60_000);
  if (minutes < 60) return `in ${minutes} min`;
  return `at ${new Date(value).toLocaleTimeString(undefined, {
    hour: "2-digit",
    minute: "2-digit",
  })}`;
}

export function Decisions({
  organizationId,
  robots,
  getToken,
  refresh,
  executionId,
  role,
}: {
  organizationId: string;
  robots: { id: string; name: string }[] | null;
  getToken: () => Promise<string>;
  refresh: number;
  executionId?: string;
  role?: string;
}) {
  const { interactions, interactionError, submittingInteraction, respond } =
    useDecisions(organizationId, getToken, refresh, executionId);
  return (
    <section
      className={`hitl-overview ${interactions?.length ? "hitl-overview--attention" : interactions?.length === 0 ? "hitl-overview--empty" : ""}`}
      aria-labelledby="hitl-heading"
    >
      <div className="hitl-overview__header">
        <div>
          <span className="panel-kicker">Human decisions</span>
          <h2 id="hitl-heading">
            Operator requests
            {interactions && (
              <span className="heading-count">
                {interactions.length.toString()}
              </span>
            )}
          </h2>
        </div>
        <span className="hitl-overview__state">
          {interactions === null
            ? "Checking…"
            : interactions.length
              ? "Action requested"
              : "No pending requests"}
        </span>
      </div>
      {interactionError && (
        <p className="hitl-overview__error" role="alert">
          {interactionError}
        </p>
      )}
      {interactions === null ? (
        <p className="hitl-overview__empty">Loading operator requests…</p>
      ) : interactions.length === 0 ? (
        <p className="hitl-overview__empty">No pending operator requests.</p>
      ) : (
        <div className="hitl-request-list">
          {interactions.map((interaction) => {
            const robot = robots?.find(
              (item) => item.id === interaction.robot_id,
            );
            const submitted = interaction.response?.status === "submitted";
            return (
              <article className="hitl-request" key={interaction.request_id}>
                <div className="hitl-request__copy">
                  <div className="hitl-request__meta">
                    <span>{robot?.name ?? interaction.robot_id}</span>
                    <span>
                      {interaction.deadline
                        ? `Deadline ${deadlineLabel(interaction.deadline)}`
                        : "No timeout"}
                    </span>
                  </div>
                  <h3>{interaction.prompt}</h3>
                  <a
                    className="text-action"
                    href={resourcePath(
                      "runs",
                      interaction.execution_id,
                      organizationId,
                    )}
                  >
                    Inspect run
                  </a>
                  {submitted && (
                    <p className="hitl-request__submitted">
                      Response submitted. Waiting for device confirmation.
                    </p>
                  )}
                </div>
                <div className="hitl-request__actions">
                  {interaction.choices.map((choice) => (
                    <button
                      type="button"
                      key={choice.value}
                      className="button-secondary"
                      disabled={
                        role === "viewer" ||
                        !!interactionError ||
                        submitted ||
                        submittingInteraction === interaction.request_id
                      }
                      onClick={() => void respond(interaction, choice.value)}
                    >
                      {choice.label}
                    </button>
                  ))}
                </div>
              </article>
            );
          })}
        </div>
      )}
    </section>
  );
}

export function useDecisions(
  organizationId: string,
  getToken: () => Promise<string>,
  refresh: number,
  executionId?: string,
) {
  const [interactions, setInteractions] = useState<Interaction[] | null>(null);
  const [interactionError, setInteractionError] = useState<string | null>(null);
  const [submittingInteraction, setSubmittingInteraction] = useState("");
  const responseKeys = useRef<Record<string, string>>({});
  // Pending requests are an attention queue, so keep them fresher than the rest
  // of the dashboard snapshot. A submitted response remains visible until the
  // daemon confirms a terminal state.
  // biome-ignore lint/correctness/useExhaustiveDependencies: refresh explicitly requests a new snapshot
  useEffect(() => {
    if (!organizationId) return;
    let cancelled = false;
    let timer: number | undefined;
    const load = async () => {
      try {
        const items = await apiFetchAll<Interaction>(
          getToken,
          `/v1/organizations/${organizationId}/interactions?state=pending${executionId ? "&execution_id=" + encodeURIComponent(executionId) : ""}`,
        );
        if (!cancelled) {
          setInteractions(items);
          setInteractionError(null);
        }
      } catch (error) {
        if (!cancelled) {
          setInteractionError(
            error instanceof Error
              ? error.message
              : "Human interaction queue is unavailable.",
          );
        }
      } finally {
        if (!cancelled) timer = window.setTimeout(load, 3000);
      }
    };
    setInteractions(null);
    void load();
    return () => {
      cancelled = true;
      if (timer !== undefined) window.clearTimeout(timer);
    };
  }, [organizationId, getToken, refresh, executionId]);

  const respond = async (interaction: Interaction, choice: string) => {
    const key =
      responseKeys.current[interaction.request_id] ?? crypto.randomUUID();
    responseKeys.current[interaction.request_id] = key;
    setSubmittingInteraction(interaction.request_id);
    setInteractionError(null);
    try {
      const accepted = (await apiFetch(
        getToken,
        `/v1/interactions/${interaction.request_id}/responses`,
        {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            "Idempotency-Key": key,
          },
          body: JSON.stringify({ chosen_value: choice }),
        },
      ).then((response) => response.json())) as {
        response_id: string;
        status: "submitted";
      };
      setInteractions(
        (current) =>
          current?.map((item) =>
            item.request_id === interaction.request_id
              ? {
                  ...item,
                  response: {
                    response_id: accepted.response_id,
                    chosen_value: choice,
                    status: "submitted",
                  },
                }
              : item,
          ) ?? null,
      );
    } catch (error) {
      setInteractionError(
        error instanceof Error ? error.message : "Unable to submit response.",
      );
    } finally {
      setSubmittingInteraction("");
    }
  };

  return { interactions, interactionError, submittingInteraction, respond };
}
