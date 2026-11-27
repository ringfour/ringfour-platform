import { activeRun } from "@/lib/app-history";
import type { AppRun } from "@/lib/apps";
export const groups = ["Active", "Needs attention", "Finished", "All"] as const;
export function duration(run: AppRun) {
  if (!run.started_at) return "Not started";
  if (!run.ended_at) return activeRun(run) ? "In progress" : "End unconfirmed";
  const seconds = Math.max(
    0,
    Math.round((Date.parse(run.ended_at) - Date.parse(run.started_at)) / 1000),
  );
  return seconds < 60
    ? `${seconds}s`
    : `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
}
