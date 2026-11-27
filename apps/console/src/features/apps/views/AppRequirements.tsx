import type { Compatibility, Revision, Robot } from "@/lib/apps";
import { capabilityInstance, describeCapability } from "@/lib/capabilities";
import type { AgentInventory } from "@/lib/robot-status";
export function AppRequirements({
  revision,
  robot,
  compatibility,
  inventory,
}: {
  revision: Revision;
  robot?: Robot;
  compatibility?: Compatibility | string;
  inventory?: AgentInventory;
}) {
  return (
    <div className="store-requirements">
      {(["required", "optional"] as const).map((kind) => (
        <section key={kind}>
          <h3>{kind === "required" ? "Required" : "Optional"}</h3>
          {revision.manifest.capabilities[kind].length ? (
            <ul>
              {revision.manifest.capabilities[kind].map((item) => {
                const { icon: Icon, name } = describeCapability(
                  item.capability,
                );
                const fit =
                  typeof compatibility === "object" ? compatibility : undefined;
                const missing = fit?.[
                  kind === "required" ? "missing_required" : "missing_optional"
                ].some(
                  (c) =>
                    c.capability === item.capability &&
                    c.instance_kind === item.instance_kind,
                );
                const observed =
                  inventory &&
                  inventory.observed_at === fit?.inventory_observed_at
                    ? inventory
                    : undefined;
                const instances = observed?.instances?.[
                  item.capability
                ]?.filter(
                  (n) => !item.instance_kind || n === item.instance_kind,
                );
                const detail = !robot
                  ? ""
                  : missing
                    ? `${robot.name} does not report this capability`
                    : observed?.capabilities.includes(item.capability) ||
                        fit?.status === "compatible"
                      ? `Available on ${robot.name}${instances?.length ? " · " + instances.map((n) => capabilityInstance(item.capability, n)).join(", ") : ""}`
                      : "Availability unconfirmed";
                return (
                  <li
                    key={item.capability + item.instance_kind}
                    className="flex items-center gap-2"
                  >
                    <Icon size={18} aria-hidden="true" />{" "}
                    <span>
                      {name}
                      {item.instance_kind
                        ? ` · ${item.instance_kind.replaceAll("_", " ")}`
                        : ""}
                      {detail && (
                        <small className="block text-muted-foreground">
                          {detail}
                        </small>
                      )}
                    </span>
                  </li>
                );
              })}
            </ul>
          ) : (
            <p>None</p>
          )}
        </section>
      ))}
    </div>
  );
}
