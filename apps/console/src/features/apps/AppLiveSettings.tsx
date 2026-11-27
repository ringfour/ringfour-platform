import { useCallback, useEffect, useRef, useState } from "react";
import { SchemaFields } from "@/components/forms/SchemaFields";
import type { AppLogbookEvent, AppManifest } from "@/lib/apps";
import { mutateApp, readJson, readPage, type Token } from "@/lib/apps";
import { StudioSection } from "@/views/StudioPage";

type SettingsRevision = {
  id: string;
  revision_id: string;
  values: Record<string, string | number | boolean>;
  created_at: string;
};
type SettingsApplication = {
  application_id: string;
  run_id: string;
  values: Record<string, string | number | boolean>;
  state: "pending" | "applied" | "rejected" | "ended_before_confirmation";
  initial: boolean;
  requested_at: string;
  reason: string | null;
  delivery_state: string;
};
type SettingsState = {
  installation_id: string;
  support: "supported" | "unsupported" | "upgrade_required";
  runtime_settings: AppManifest["runtime_settings"] | null;
  selected_for_next_start: SettingsRevision | null;
  active_run_id: string | null;
  last_applied: SettingsRevision | null;
  pending_application: SettingsApplication | null;
};

const errorText = (error: unknown) =>
  error instanceof Error ? error.message : "Settings request failed.";
const currentValues = (state: SettingsState) =>
  (state.active_run_id
    ? (state.last_applied?.values ?? state.selected_for_next_start?.values)
    : (state.selected_for_next_start?.values ?? state.last_applied?.values)) ??
  state.runtime_settings?.defaults ??
  {};

export function AppLiveSettings({
  installationId,
  organizationId,
  token,
  canManage,
  onDirtyChange,
}: {
  installationId: string;
  organizationId: string;
  robotName: string;
  token: Token;
  canManage: boolean;
  onDirtyChange: (dirty: boolean) => void;
}) {
  const [state, setState] = useState<SettingsState | null>(null);
  const [values, setValues] = useState<
    Record<string, string | number | boolean>
  >({});
  const [dirty, setDirty] = useState(false);
  const dirtyRef = useRef(false);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [feedback, setFeedback] = useState("");
  const [refresh, setRefresh] = useState(0);
  const wasPending = useRef(false);
  const outcomeChecked = useRef(false);
  const [lastOutcome, setLastOutcome] = useState<AppLogbookEvent | null>(null);
  const load = useCallback(async () => {
    return readJson<SettingsState>(
      token,
      `/v1/app-installations/${installationId}/settings`,
    );
  }, [installationId, token]);

  // Reload when the target changes, on explicit refresh, and while an application is pending.
  useEffect(() => {
    // This state is an explicit reload signal, including for the pending poll.
    void refresh;
    let live = true;
    setLoading(true);
    setError("");
    void load()
      .then((next) => {
        if (!live) return;
        setState(next);
        if (
          !next.pending_application &&
          (wasPending.current || !outcomeChecked.current)
        ) {
          outcomeChecked.current = true;
          void readPage<AppLogbookEvent>(
            token,
            `/v1/organizations/${organizationId}/app-logbook`,
          )
            .then((events) => {
              if (!live) return;
              const outcome = events
                .filter(
                  (event) =>
                    event.installation_id === installationId &&
                    [
                      "app.settings.applied",
                      "app.settings.rejected",
                      "app.settings.ended_before_confirmation",
                    ].includes(event.type),
                )
                .sort((left, right) =>
                  right.occurred_at.localeCompare(left.occurred_at),
                )[0];
              if (outcome) setLastOutcome(outcome);
            })
            .catch(() => {
              if (live) {
                outcomeChecked.current = false;
                setError(
                  "Settings outcome history is unavailable. Current values are still shown.",
                );
              }
            });
        }
        wasPending.current = next.pending_application?.state === "pending";
        if (!dirtyRef.current) {
          setValues(currentValues(next));
        }
      })
      .catch((cause) => {
        if (live) setError(errorText(cause));
      })
      .finally(() => {
        if (live) setLoading(false);
      });
    return () => {
      live = false;
    };
  }, [installationId, load, organizationId, refresh, token]);
  useEffect(() => {
    if (state?.pending_application?.state !== "pending") return;
    const timer = window.setInterval(
      () => setRefresh((current) => current + 1),
      5000,
    );
    return () => window.clearInterval(timer);
  }, [state?.pending_application?.state]);

  async function save(event: React.FormEvent) {
    event.preventDefault();
    if (!state || !canManage || state.support !== "supported") return;
    setSaving(true);
    setError("");
    setFeedback("");
    setLastOutcome(null);
    try {
      const result = await mutateApp<
        | { state: "saved_for_next_start"; settings_revision_id: string }
        | { state: "pending"; application_id: string; run_id: string }
      >(token, `/v1/app-installations/${installationId}/settings`, {
        values,
        expected_selected_revision_id:
          state.selected_for_next_start?.id ?? null,
        expected_applied_revision_id: state.last_applied?.id ?? null,
        expected_run_id: state.active_run_id,
      });
      setDirty(false);
      dirtyRef.current = false;
      onDirtyChange(false);
      setFeedback(
        result.state === "saved_for_next_start"
          ? "Saved for next start. The App has not confirmed these values yet."
          : "Settings requested. Waiting for the App to confirm application.",
      );
      const next = await load();
      setState(next);
      wasPending.current = next.pending_application?.state === "pending";
      setValues(currentValues(next));
    } catch (cause) {
      setError(errorText(cause));
    } finally {
      setSaving(false);
    }
  }

  const schema = state?.runtime_settings?.schema;
  const canEdit = Boolean(
    canManage &&
      state?.support === "supported" &&
      !state.pending_application &&
      !saving,
  );
  return (
    <StudioSection
      title="Live settings"
      titleId="live-settings-title"
      width="form"
      description="Applies while the App is running after it confirms the change. When idle, values are saved for the next start."
      actions={
        <button
          className="button-secondary"
          type="button"
          onClick={() => setRefresh((current) => current + 1)}
        >
          Refresh status
        </button>
      }
    >
      <div className="studio-form">
        {loading && !state && <p role="status">Loading live settings…</p>}
        {error && (
          <p role="alert" className="app-feedback app-feedback-error">
            {error}
          </p>
        )}
        {feedback && (
          <p role="status" className="app-feedback">
            {feedback}
          </p>
        )}
        {state?.support === "unsupported" && (
          <p className="app-settings-notice">
            This App release does not declare live settings. Startup
            configuration remains available above.
          </p>
        )}
        {state?.support === "upgrade_required" && (
          <p className="app-settings-notice">
            The selected robot needs an App runtime upgrade before live settings
            can be changed. Stored values and confirmed outcomes are shown
            below.
          </p>
        )}
        {state?.pending_application && (
          <p role="status" aria-live="polite" className="app-settings-pending">
            {state.pending_application.initial
              ? "App running · initial settings pending."
              : "Applying settings · awaiting App confirmation."}
            {state.pending_application.reason
              ? ` ${state.pending_application.reason}`
              : ""}
            <small>
              {" "}
              Delivery:{" "}
              {state.pending_application.delivery_state.replaceAll("_", " ")}.
            </small>
          </p>
        )}
        {!state?.pending_application &&
          lastOutcome?.type === "app.settings.rejected" && (
            <p role="alert" className="app-feedback app-feedback-error">
              The App rejected the settings for the previous request.
              {typeof lastOutcome.payload.reason === "string"
                ? ` ${lastOutcome.payload.reason}`
                : ""}
            </p>
          )}
        {!state?.pending_application &&
          lastOutcome?.type === "app.settings.ended_before_confirmation" && (
            <p role="status" className="app-settings-pending">
              The run ended before this settings request was confirmed. The
              prior next-start selection is unchanged.
            </p>
          )}
        {!state?.pending_application &&
          lastOutcome?.type === "app.settings.applied" && (
            <p role="status" className="app-settings-confirmed">
              The App confirmed its last settings request.
            </p>
          )}
        {state?.runtime_settings && state.support === "supported" && (
          <form
            className="studio-form app-live-settings-form"
            onSubmit={(event) => void save(event)}
          >
            <SchemaFields
              schema={schema ?? { properties: {} }}
              values={values}
              prefix={`${installationId}-live`}
              disabled={!canEdit}
              markRequired
              onChange={(next) => {
                setDirty(true);
                dirtyRef.current = true;
                onDirtyChange(true);
                setValues(next);
              }}
              feedback={(key) => (
                <>
                  {state.active_run_id && (
                    <p>
                      Confirmed:{" "}
                      {state.last_applied && key in state.last_applied.values
                        ? String(state.last_applied.values[key])
                        : "Not confirmed for this run"}
                    </p>
                  )}
                  <p>
                    Next start:{" "}
                    {state.selected_for_next_start &&
                    key in state.selected_for_next_start.values
                      ? String(state.selected_for_next_start.values[key])
                      : "App default"}
                  </p>
                  {state.pending_application &&
                    key in state.pending_application.values && (
                      <p className="studio-field-feedback" data-state="pending">
                        Requested:{" "}
                        {String(state.pending_application.values[key])} ·
                        Awaiting App confirmation
                      </p>
                    )}
                  {!state.pending_application &&
                    lastOutcome?.type === "app.settings.rejected" && (
                      <p
                        className="studio-field-feedback"
                        data-state="rejected"
                      >
                        Last settings request rejected
                        {typeof lastOutcome.payload.reason === "string"
                          ? `: ${lastOutcome.payload.reason}`
                          : ""}
                      </p>
                    )}
                </>
              )}
            />
            <div className="app-actions">
              <button
                className="button-primary"
                type="submit"
                disabled={!canEdit || !dirty}
              >
                {saving
                  ? "Saving…"
                  : state.active_run_id
                    ? "Apply live settings"
                    : "Save for next start"}
              </button>
              {!canManage && (
                <span>Developer authority is required to change settings.</span>
              )}
              {dirty && state && (
                <button
                  className="button-secondary"
                  type="button"
                  disabled={saving}
                  onClick={() => {
                    setValues(currentValues(state));
                    setDirty(false);
                    dirtyRef.current = false;
                    onDirtyChange(false);
                    setError("");
                    setFeedback("");
                  }}
                >
                  Discard edits
                </button>
              )}
              {state.pending_application && (
                <span>
                  Wait for this request to resolve or stop this exact run before
                  changing settings again.
                </span>
              )}
            </div>
          </form>
        )}
        {state && state.support !== "unsupported" && (
          <details className="app-technical-details">
            <summary>Settings revision details</summary>
            <dl className="studio-value-list">
              <div>
                <dt>Next-start revision</dt>
                <dd>{state.selected_for_next_start?.id ?? "None selected"}</dd>
              </div>
              <div>
                <dt>Confirmed revision</dt>
                <dd>{state.last_applied?.id ?? "None confirmed"}</dd>
              </div>
              {state.last_applied && (
                <div>
                  <dt>Last confirmation</dt>
                  <dd>
                    {new Date(state.last_applied.created_at).toLocaleString()}
                  </dd>
                </div>
              )}
            </dl>
          </details>
        )}
      </div>
    </StudioSection>
  );
}
