import { StudioTime } from "./StudioTime";

export type ResourceSample = {
  observedAt: string;
  stale: boolean;
  battery?: {
    percent?: number;
    supply?: "charging" | "discharging" | "external";
  };
  storage?: { freeBytes: number; totalBytes: number };
};

export function RobotResources({ sample }: { sample?: ResourceSample }) {
  if (!sample) return <small>Battery and storage unreported</small>;
  const percent = sample.battery?.percent;
  const storage = sample.storage;
  const gib = (bytes: number) =>
    (bytes / 1024 ** 3).toLocaleString(undefined, {
      maximumFractionDigits: 1,
    }) + " GiB";
  return (
    <div className="studio-resources">
      <small>
        {sample.stale ? "Last known resources" : "Resources observed"} ·{" "}
        <StudioTime value={sample.observedAt} />
      </small>
      <small>
        Battery{" "}
        {typeof percent === "number"
          ? percent + "%"
          : sample.battery?.supply === "external"
            ? "not applicable"
            : "unknown"}{" "}
        {sample.battery?.supply}
      </small>
      {typeof percent === "number" && (
        <meter
          aria-label="Battery remaining"
          min={0}
          max={100}
          value={percent}
        />
      )}
      <small>
        {storage
          ? gib(storage.freeBytes) + " free of " + gib(storage.totalBytes)
          : "Storage unknown"}
      </small>
      {storage && storage.totalBytes > 0 && (
        <meter
          aria-label="Storage free"
          min={0}
          max={storage.totalBytes}
          value={storage.freeBytes}
        />
      )}
    </div>
  );
}
