/** AP-03 inventory projection: compatibility reads this exact current snapshot. */
export type AgentInventory = {
  observed_at: string;
  source: {
    kind: string;
    profile: { name: string; model: string; version: string };
  };
  capabilities: string[];
  instances: Record<string, string[]>;
};
export type RobotAppStatus = {
  connectivity: "online" | "offline";
  last_seen_at: string | null;
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
export function inventoryObservation(status?: RobotAppStatus) {
  return status?.inventory?.current ?? status?.inventory?.last_successful;
}
