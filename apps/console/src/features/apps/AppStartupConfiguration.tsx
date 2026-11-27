import type { FormEvent, ReactNode } from "react";
import { SchemaFields } from "@/components/forms/SchemaFields";
import type { Installation, Revision } from "@/lib/apps";
import { StudioSection } from "@/views/StudioPage";
import type { StartupEdit } from "./model";

type Values = Record<string, string | number | boolean>;
export function AppStartupConfiguration({
  installation,
  revision,
  values,
  editing,
  dirty,
  active,
  canManage,
  error,
  onEdit,
  onChange,
  onSave,
  onCancel,
  feedback,
}: {
  installation?: Installation;
  revision?: Revision;
  values: Values;
  editing: StartupEdit | null;
  dirty: boolean;
  active: boolean;
  canManage: boolean;
  error: string;
  onEdit: () => void;
  onChange: (values: Values) => void;
  onSave: (event: FormEvent) => void;
  onCancel: () => void;
  feedback?: ReactNode;
}) {
  const schema = editing?.revision.manifest.configuration.schema;
  const environment =
    (editing?.revision ?? revision)?.manifest.configuration.environment ?? {};
  const envFor = (key: string) =>
    Object.entries(environment)
      .filter(([, field]) => field === key)
      .map(([name]) => name)
      .join(", ");
  return (
    <StudioSection
      title="Startup configuration"
      titleId="startup-configuration-title"
      width="form"
      description="Requires an App restart. Stop the App before editing these values."
      actions={
        canManage && installation?.installed && !editing ? (
          <button
            type="button"
            className="button-secondary"
            disabled={active}
            aria-describedby={active ? "startup-active" : undefined}
            onClick={onEdit}
          >
            Edit startup configuration
          </button>
        ) : undefined
      }
    >
      <div className="studio-form">
        {error && (
          <p role="alert" className="app-feedback app-feedback-error">
            {error}
          </p>
        )}
        {active && (
          <p id="startup-active" className="app-muted">
            Stop this installation’s active run before changing startup
            configuration.
          </p>
        )}
        {!installation ? (
          <p>Select a robot installation above to inspect its configuration.</p>
        ) : !installation.installed ? (
          <p>This installation does not have an installed release.</p>
        ) : editing ? (
          <form className="studio-form" onSubmit={onSave}>
            <SchemaFields
              schema={schema ?? { properties: {} }}
              values={editing.values}
              prefix={installation.id}
              onChange={onChange}
              feedback={(key) =>
                envFor(key)
                  ? `Passed as ${envFor(key)} at the next start`
                  : undefined
              }
            />
            <div className="app-actions">
              <button
                className="button-primary"
                type="submit"
                disabled={!dirty}
              >
                Save startup configuration
              </button>
              <button
                className="button-secondary"
                type="button"
                onClick={onCancel}
              >
                Cancel
              </button>
            </div>
          </form>
        ) : (
          <dl className="studio-value-list">
            {Object.entries(values).map(([key, value]) => (
              <div key={key}>
                <dt>
                  {key.replaceAll("_", " ")}
                  {envFor(key) && (
                    <small className="studio-field-feedback">
                      Environment: {envFor(key)}
                    </small>
                  )}
                </dt>
                <dd>{String(value)}</dd>
              </div>
            ))}
          </dl>
        )}
        {installation?.installed && !editing && !Object.keys(values).length && (
          <p>No startup values are selected.</p>
        )}
        {installation?.installed && (
          <details className="app-technical-details">
            <summary>Configuration revision details</summary>
            <p>Release {revision?.manifest.release ?? "unavailable"}</p>
            <code>{installation.installed.configuration_revision_id}</code>
          </details>
        )}
        {feedback}
      </div>
    </StudioSection>
  );
}
