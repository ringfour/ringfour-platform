// AP-01 App response readers. Product routes remain disabled until AP-11.
export type AppOperationState =
  | "queued"
  | "accepted"
  | "running"
  | "succeeded"
  | "failed"
  | "cancelled";
export type AppOperationPhase =
  | "queued"
  | "accepted"
  | "checking"
  | "staging"
  | "staged"
  | "applied"
  | "starting"
  | "observed_running"
  | "stopping"
  | "stopped"
  | "failed"
  | "cancelled";
export interface AppOperationSummary {
  operation_id: string;
  state: AppOperationState;
  phase: AppOperationPhase;
  run_id: string | null;
  observed_at: string | null;
  failure_code: string | null;
}
export interface AppFailureSummary {
  code: string;
  message: string;
  retryable: boolean;
}
const states = [
  "queued",
  "accepted",
  "running",
  "succeeded",
  "failed",
  "cancelled",
] as const;
const phases = [
  "queued",
  "accepted",
  "checking",
  "staging",
  "staged",
  "applied",
  "starting",
  "observed_running",
  "stopping",
  "stopped",
  "failed",
  "cancelled",
] as const;
function record(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value))
    throw new Error("Invalid App response");
  return value as Record<string, unknown>;
}
function text(value: unknown): string {
  if (typeof value !== "string" || value.length === 0)
    throw new Error("Invalid App string");
  return value;
}
function id(value: unknown): string {
  const result = text(value);
  if (
    result.length !== 36 ||
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(
      result,
    )
  )
    throw new Error("Invalid App ID");
  return result;
}
function nullable<T>(value: unknown, read: (value: unknown) => T): T | null {
  return value === null ? null : read(value);
}
function timestamp(value: unknown): string {
  const result = text(value);
  if (
    /\s/.test(result) ||
    !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,6})?Z$/.test(result)
  )
    throw new Error("Invalid App timestamp");
  const date = new Date(result);
  if (
    !Number.isFinite(date.getTime()) ||
    date.toISOString().slice(0, 19) !== result.slice(0, 19)
  )
    throw new Error("Invalid App timestamp");
  return result;
}
export function readAppFailure(value: unknown): AppFailureSummary {
  const failure = record(value),
    details = record(failure.details);
  const code = text(failure.code),
    message = text(failure.message);
  if (
    !/^[a-z][a-z0-9_]*$/.test(code) ||
    /\s/.test(code) ||
    [...message].length > 1000 ||
    typeof details.retryable !== "boolean"
  )
    throw new Error("Invalid App failure");
  return { code, message, retryable: details.retryable };
}
export function readAppOperation(value: unknown): AppOperationSummary {
  const operation = record(value);
  if (
    !states.includes(operation.state as AppOperationState) ||
    !phases.includes(operation.phase as AppOperationPhase)
  )
    throw new Error("Unknown App operation state or phase");
  const state = operation.state as AppOperationState,
    phase = operation.phase as AppOperationPhase;
  const observed_at = nullable(operation.observed_at, timestamp);
  const failure_code =
    operation.failure === null ? null : readAppFailure(operation.failure).code;
  if (
    (["queued", "accepted"].includes(state) &&
      (phase !== state || observed_at !== null)) ||
    (state === "running" &&
      (!["checking", "staging", "staged", "starting", "stopping"].includes(
        phase,
      ) ||
        observed_at === null)) ||
    (state === "succeeded" &&
      (!["applied", "observed_running", "stopped"].includes(phase) ||
        observed_at === null)) ||
    (state === "failed" && (phase !== "failed" || failure_code === null)) ||
    (state === "cancelled" && phase !== "cancelled") ||
    (!["failed", "cancelled"].includes(state) && failure_code !== null)
  )
    throw new Error("Inconsistent App observation");
  return {
    operation_id: id(operation.id),
    state,
    phase,
    run_id: nullable(operation.run_id, id),
    observed_at,
    failure_code,
  };
}
