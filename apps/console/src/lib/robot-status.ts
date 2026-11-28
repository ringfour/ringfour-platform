/** AP-03 inventory projection: compatibility reads this exact current snapshot. */
export type AgentInventory = {
  observed_at: string;
  source: {
    kind: string;
    agent_version?: string | null;
    profile: { name: string; model: string; version: string };
  };
  capabilities: string[];
  instances: Record<string, string[]>;
};
export type RobotAppStatus = {
  connectivity: "online" | "offline";
  last_seen_at: string | null;
  daemon_version?: string | null;
  recommended_daemon_version?: string | null;
  recommended_agent_version?: string | null;
  support: string;
  supports_app_uninstall?: boolean | null;
  received_at: string | null;
  platform?: {
    os: string;
    architecture: string;
    native_facts: string[];
  } | null;
  inventory: null | {
    state: "available" | "unavailable";
    attempted_at: string;
    current?: AgentInventory;
    last_successful?: AgentInventory;
    reason?: string;
  };
};
function releaseStatus(installed?: string | null, recommended?: string | null) {
  const parse = (value?: string | null) =>
    value && /^\d+\.\d+\.\d+$/.test(value)
      ? value.split(".").map(Number)
      : null;
  const current = parse(installed);
  const target = parse(recommended);
  if (!current || !target) return "unknown";
  if (current[0] > target[0]) return "incompatible";
  for (let i = 0; i < 3; i += 1) {
    if (current[i] < target[i]) return "update available";
    if (current[i] > target[i]) return "incompatible";
  }
  return "current";
}
export function daemonUpdate(status?: RobotAppStatus) {
  return status?.connectivity === "online"
    ? releaseStatus(status.daemon_version, status.recommended_daemon_version)
    : "unknown";
}
export function agentUpdate(status?: RobotAppStatus) {
  if (
    status?.connectivity === "online" &&
    status.inventory?.reason === "unsupported_agent_protocol"
  )
    return "incompatible";
  return status?.connectivity === "online" &&
    status.inventory?.state === "available"
    ? releaseStatus(
        status.inventory.current?.source.agent_version,
        status.recommended_agent_version,
      )
    : "unknown";
}
export function inventoryObservation(status?: RobotAppStatus) {
  return status?.inventory?.current ?? status?.inventory?.last_successful;
}
