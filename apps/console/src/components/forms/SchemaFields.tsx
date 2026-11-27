import type { ReactNode } from "react";
import type { AppSchema, AppValues } from "@/lib/apps";

/** Controlled App-declared fields shared by startup, runtime and release forms. */
export function SchemaFields({
  schema,
  values,
  onChange,
  prefix,
  disabled = false,
  markRequired = false,
  feedback,
}: {
  schema: AppSchema;
  values: AppValues;
  onChange: (values: AppValues) => void;
  prefix: string;
  disabled?: boolean;
  markRequired?: boolean;
  feedback?: (key: string) => ReactNode;
}) {
  return (
    <>
      {Object.entries(schema.properties).map(([key, rule]) => {
        const id = `${prefix}-${key}`;
        const required = schema.required?.includes(key) ?? false;
        const help = feedback?.(key);
        const describedBy = help ? `${id}-feedback` : undefined;
        const change = (value: string | number | boolean | undefined) => {
          const next = { ...values };
          if (value === undefined) delete next[key];
          else next[key] = value;
          onChange(next);
        };
        return (
          <div className="app-field" key={key}>
            <label htmlFor={id}>
              {key.replaceAll("_", " ")}
              {markRequired && required ? " · required" : ""}
            </label>
            {rule.enum ? (
              <select
                id={id}
                required={required}
                disabled={disabled}
                value={String(values[key] ?? "")}
                aria-describedby={describedBy}
                onChange={(event) =>
                  change(
                    rule.enum?.find(
                      (value) => String(value) === event.target.value,
                    ),
                  )
                }
              >
                {!required && <option value="">Not set</option>}
                {rule.enum.map((value) => (
                  <option key={String(value)} value={String(value)}>
                    {String(value)}
                  </option>
                ))}
              </select>
            ) : rule.type === "boolean" ? (
              <input
                id={id}
                type="checkbox"
                checked={values[key] === true}
                disabled={disabled}
                aria-describedby={describedBy}
                onChange={(event) => change(event.target.checked)}
              />
            ) : (
              <input
                id={id}
                required={required}
                disabled={disabled}
                type={
                  rule.type === "number" || rule.type === "integer"
                    ? "number"
                    : "text"
                }
                step={
                  rule.type === "number"
                    ? "any"
                    : rule.type === "integer"
                      ? "1"
                      : undefined
                }
                min={
                  rule.minimum ??
                  (rule.type === "number" || rule.type === "integer"
                    ? -Number.MAX_SAFE_INTEGER
                    : undefined)
                }
                max={
                  rule.maximum ??
                  (rule.type === "number" || rule.type === "integer"
                    ? Number.MAX_SAFE_INTEGER
                    : undefined)
                }
                minLength={rule.minLength}
                maxLength={rule.maxLength}
                value={String(values[key] ?? "")}
                aria-describedby={describedBy}
                onChange={(event) =>
                  change(
                    rule.type === "number" || rule.type === "integer"
                      ? event.target.value === ""
                        ? undefined
                        : Number(event.target.value)
                      : event.target.value,
                  )
                }
              />
            )}
            {help && (
              <div id={describedBy} className="studio-field-feedback">
                {help}
              </div>
            )}
          </div>
        );
      })}
    </>
  );
}
