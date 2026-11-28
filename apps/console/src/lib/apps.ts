import { apiFetch, apiFetchAll } from "@/lib/api";

export type Token = () => Promise<string>;
export type Organization = { id: string; name: string; role: string };
export type Robot = {
  id: string;
  organization_id: string;
  name: string;
  appearance?: string | null;
  created_at?: string;
};
export type Capability = { capability: string; instance_kind?: string };
export type AppValues = Record<string, string | number | boolean>;
export type AppField = {
  type: "string" | "integer" | "number" | "boolean";
  minimum?: number;
  maximum?: number;
  minLength?: number;
  maxLength?: number;
  enum?: (string | number | boolean)[];
};
export type AppSchema = {
  properties: Record<string, AppField>;
  required?: string[];
};
export type AppManifest = {
  app_key: string;
  release: string;
  display: { name: string; description: string };
  capabilities: { required: Capability[]; optional: Capability[] };
  targets: { os: string; architecture: string; image: string }[];
  configuration: {
    environment?: Record<string, string>;
    defaults: AppValues;
    schema: AppSchema;
  };
  runtime_settings?: { defaults: AppValues; schema: AppSchema };
};
export type App = {
  id: string;
  publisher_organization_id: string;
  publisher_name?: string;
  publisher_user_name?: string;
  preview_revision_id?: string;
  publisher_type?: "ringfour" | "workspace";
  app_key: string;
  name: string;
  description: string;
  created_at: string;
};
export type Revision = {
  id: string;
  app_id: string;
  publisher_organization_id: string;
  manifest: AppManifest;
  manifest_digest: string;
  images?: {
    repository: string;
    digest: string;
    size_bytes: number;
    platform: "linux/amd64" | "linux/arm64";
  }[];
  visibility: "listed" | "unlisted";
  published_at: string;
};
export type AppImport = {
  id: string;
  organization_id: string;
  app_id: string;
  revision_id: string;
  publisher_organization_id: string;
  source: { kind: "store" } | { kind: "direct_share"; share_id: string };
  imported_at: string;
};
export type Binding = {
  revision_id: string;
  configuration_revision_id: string;
  image: string;
  installed_at: string;
  observed_at: string;
};
export type Candidate = {
  operation_id: string;
  revision_id: string;
  configuration_revision_id: string;
  state: "staging" | "staged" | "failed";
  failure: {
    code: string;
    message: string;
    details?: { retryable?: boolean };
  } | null;
};
export type Installation = {
  id: string;
  organization_id: string;
  robot_id: string;
  app_id: string;
  created_at: string;
  removed_at?: string | null;
  available_revision_id: string | null;
  installed: Binding | null;
  candidate: Candidate | null;
  last_observed_at: string | null;
};
export type AppOperation = {
  id: string;
  organization_id: string;
  robot_id: string;
  installation_id: string;
  run_id: string | null;
  kind:
    | "install"
    | "configure"
    | "update"
    | "rollback"
    | "uninstall"
    | "start"
    | "stop";
  state: string;
  phase: string;
  requested_at: string;
  acknowledged_at: string | null;
  observed_at: string | null;
  completed_at: string | null;
  failure: {
    code: string;
    message: string;
    details?: { retryable?: boolean };
  } | null;
};
export type AppRun = {
  id: string;
  organization_id: string;
  robot_id: string;
  installation_id: string;
  start_operation_id: string;
  snapshot: {
    development?: {
      session_id: string;
      generation: number;
      archive_sha256: string;
      expires_at: string;
    };
    app_id: string;
    revision_id: string;
    publisher_organization_id: string;
    configuration_revision_id: string;
    configuration: Record<string, string | number | boolean>;
    image: string;
  };
  state:
    | "accepted"
    | "running"
    | "succeeded"
    | "failed"
    | "stopped"
    | "interrupted";
  requested_by: string;
  requested_at: string;
  started_at: string | null;
  ended_at: string | null;
  completed_at?: string | null;
  observed_at: string | null;
  exit_code: number | null;
  failure: { code: string; message: string } | null;
};
export type Compatibility = {
  status:
    | "compatible"
    | "daemon_upgrade_required"
    | "inventory_unavailable"
    | "missing_required"
    | "no_matching_image"
    | "native_requirement_unavailable";
  checked_at: string;
  inventory_observed_at: string | null;
  missing_required: Capability[];
  missing_optional: Capability[];
  missing_native: string[];
};
export type AppLogbookEvent = {
  event_id: string;
  organization_id: string;
  robot_id: string;
  run_id: string | null;
  installation_id: string | null;
  app_id: string | null;
  revision_id: string | null;
  operation_id: string | null;
  actor_user_id: string | null;
  type: string;
  occurred_at: string;
  received_at: string;
  payload: Record<string, unknown>;
};
export type AppConfiguration = {
  id: string;
  installation_id: string;
  revision_id: string;
  values: Record<string, string | number | boolean>;
};

export const readPage = async <T>(token: Token, path: string) =>
  apiFetchAll<T>(token, path);
export async function readFirstPage<T>(
  token: Token,
  path: string,
  limit = 50,
): Promise<T[]> {
  const response = await apiFetch(
    token,
    withQuery(path, { limit: String(limit) }),
  );
  const page = (await response.json()) as { items: T[] };
  return page.items;
}

export async function readLiveRuns(token: Token, path: string) {
  const [active, recent] = await Promise.all([
    readPage<AppRun>(token, withQuery(path, { active_only: "true" })),
    readFirstPage<AppRun>(token, withQuery(path, { recent_first: "true" }), 50),
  ]);
  return [
    ...new Map([...recent, ...active].map((run) => [run.id, run])).values(),
  ];
}

export const readJson = async <T>(token: Token, path: string): Promise<T> =>
  (await apiFetch(token, path)).json() as Promise<T>;

export function withQuery(
  path: string,
  values: Record<string, string | undefined>,
) {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(values))
    if (value) query.set(key, value);
  if (!query.size) return path;
  const separator = path.includes("?") ? "&" : "?";
  return `${path}${separator}${query}`;
}

export function mutateApp<T>(
  token: Token,
  path: string,
  body: unknown,
): Promise<T> {
  return apiFetch(token, path, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": crypto.randomUUID(),
    },
    body: JSON.stringify(body),
  }).then((response) => response.json() as Promise<T>);
}

export const appStateLabel = (state: string, phase?: string) => {
  if (state === "queued") return "Accepted · waiting for device";
  if (state === "accepted") return "Accepted by device";
  if (state === "running")
    return phase ? phase.replaceAll("_", " ") : "In progress";
  if (state === "succeeded")
    return phase === "observed_running"
      ? "Running observed"
      : phase === "stopped"
        ? "Stopped"
        : "Installed";
  if (state === "failed") return "Failed";
  if (state === "cancelled") return "Cancelled";
  return state.replaceAll("_", " ");
};

export const latestRevision = (items: Revision[]) =>
  [...items].sort((a, b) => b.published_at.localeCompare(a.published_at))[0];
