import { StudioTime } from "@/components/StudioTime";
import { capabilityInstance, describeCapability } from "@/lib/capabilities";
import { inventoryObservation, type RobotAppStatus } from "@/lib/robot-status";

export function RobotCapabilities({ status }: { status?: RobotAppStatus }) {
  const inventory = inventoryObservation(status);
  const current =
    status?.connectivity === "online" &&
    status.inventory?.state === "available" &&
    !!status.inventory.current;
  return (
    <div className="studio-form">
      {!current && (
        <p className="app-muted" role="status">
          {inventory
            ? "Last known inventory · current capabilities are unconfirmed"
            : "Inventory unavailable · no capabilities have been observed"}
          {status?.inventory?.reason
            ? ` · ${status.inventory.reason.replaceAll("_", " ")}`
            : ""}
        </p>
      )}
      {inventory && (
        <>
          <p className="text-sm text-muted-foreground">
            Source:{" "}
            {inventory.source?.kind?.replaceAll("_", " ") ?? "Not reported"} ·
            Observed <StudioTime value={inventory.observed_at} />
          </p>
          {inventory.capabilities?.length ? (
            <dl className="studio-capability-grid">
              {inventory.capabilities.map((capability) => {
                const description = describeCapability(capability);
                const Icon = description.icon;
                const instances = inventory.instances?.[capability] ?? [];
                return (
                  <div className="studio-capability" key={capability}>
                    <Icon aria-hidden="true" />
                    <div>
                      <dt>{description.name}</dt>
                      {(instances.length > 0 || description.detail) && (
                        <dd>
                          {instances.length
                            ? instances
                                .map((name) =>
                                  capabilityInstance(capability, name),
                                )
                                .join(", ")
                            : description.detail}
                        </dd>
                      )}
                    </div>
                  </div>
                );
              })}
            </dl>
          ) : (
            <p>
              {current
                ? "No capabilities reported in this inventory."
                : "No capabilities reported in the last known snapshot."}
            </p>
          )}
        </>
      )}
    </div>
  );
}
