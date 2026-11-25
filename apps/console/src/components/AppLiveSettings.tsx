import { useCallback, useEffect, useRef, useState } from "react";
import type { AppLogbookEvent, AppManifest } from "@/lib/apps";
import { mutateApp, readJson, readPage, type Token } from "@/lib/apps";

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
  robotName,
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
          ).then((events) => {
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
    <section
      className="app-panel app-settings-panel"
      aria-labelledby="live-settings-title"
    >
      <header className="app-settings-heading">
        <div>
          <p className="app-eyebrow">Live settings</p>
          <h2 id="live-settings-title">Settings for {robotName}</h2>
          <p>
            Changes are applied to this running App when it confirms them. When
            idle, they are saved for the next start.
          </p>
        </div>
        <button
          className="button-secondary"
          type="button"
          onClick={() => setRefresh((current) => current + 1)}
        >
          Refresh status
        </button>
      </header>
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
          This App release does not declare live settings. Startup configuration
          remains available above.
        </p>
      )}
      {state?.support === "upgrade_required" && (
        <p className="app-settings-notice">
          The selected robot needs an App runtime upgrade before live settings
          can be changed. Stored values and confirmed outcomes are shown below.
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
            The run ended before this settings request was confirmed. The prior
            next-start selection is unchanged.
          </p>
        )}
      {!state?.pending_application &&
        lastOutcome?.type === "app.settings.applied" && (
          <p role="status" className="app-settings-confirmed">
            The App confirmed its last settings request.
          </p>
        )}
      {state && (
        <div className="app-settings-snapshots">
          <section>
            <p className="app-eyebrow">Next start</p>
            <h3>Selected values</h3>
            {state.selected_for_next_start ? (
              <dl>
                {Object.entries(state.selected_for_next_start.values).map(
                  ([key, value]) => (
                    <div key={key}>
                      <dt>{key.replaceAll("_", " ")}</dt>
                      <dd>{String(value)}</dd>
                    </div>
                  ),
                )}
              </dl>
            ) : (
              <p>No live settings are selected for the next start.</p>
            )}
          </section>
          <section>
            <p className="app-eyebrow">Current process</p>
            <h3>Last confirmed values</h3>
            {state.active_run_id && state.last_applied ? (
              <>
                <dl>
                  {Object.entries(state.last_applied.values).map(
                    ([key, value]) => (
                      <div key={key}>
                        <dt>{key.replaceAll("_", " ")}</dt>
                        <dd>{String(value)}</dd>
                      </div>
                    ),
                  )}
                </dl>
                <small>
                  Confirmed{" "}
                  {new Date(state.last_applied.created_at).toLocaleString()}
                </small>
              </>
            ) : (
              <p>
                {state.active_run_id
                  ? "The App has not confirmed settings for this run yet."
                  : "No App process is currently running."}
              </p>
            )}
          </section>
        </div>
      )}
      {state?.runtime_settings && state.support === "supported" && (
        <form
          className="app-live-settings-form"
          onSubmit={(event) => void save(event)}
        >
          {Object.entries(schema?.properties ?? {}).map(([key, property]) => {
            const fieldId = `${installationId}-live-${key}`;
            const required = schema?.required?.includes(key) ?? false;
            const change = (value: string | number | boolean) => {
              setDirty(true);
              dirtyRef.current = true;
              onDirtyChange(true);
              setValues((current) => ({ ...current, [key]: value }));
            };
            return (
              <div className="app-field" key={key}>
                <label htmlFor={fieldId}>
                  {key.replaceAll("_", " ")}
                  {required ? " · required" : ""}
                </label>
                {property.type === "boolean" ? (
                  <input
                    id={fieldId}
                    type="checkbox"
                    checked={Boolean(values[key])}
                    disabled={!canEdit}
                    onChange={(event) => change(event.target.checked)}
                  />
                ) : property.enum ? (
                  <select
                    id={fieldId}
                    required={required}
                    disabled={!canEdit}
                    value={String(values[key] ?? "")}
                    onChange={(event) =>
                      change(
                        property.type === "integer"
                          ? Number(event.target.value)
                          : event.target.value,
                      )
                    }
                  >
                    {property.enum.map((choice) => (
                      <option key={String(choice)} value={String(choice)}>
                        {String(choice)}
                      </option>
                    ))}
                  </select>
                ) : (
                  <input
                    id={fieldId}
                    required={required}
                    disabled={!canEdit}
                    type={property.type === "integer" ? "number" : "text"}
                    min={property.minimum}
                    max={property.maximum}
                    minLength={property.minLength}
                    maxLength={property.maxLength}
                    value={String(values[key] ?? "")}
                    onChange={(event) =>
                      change(
                        property.type === "integer"
                          ? Number(event.target.value)
                          : event.target.value,
                      )
                    }
                  />
                )}
              </div>
            );
          })}
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
    </section>
  );
}
