export const runStateLabel = (state: string) =>
  ({
    accepted: "Starting",
    running: "Running",
    succeeded: "Completed",
    stopped: "Stopped",
    failed: "Failed",
    interrupted: "Interrupted",
  })[state] ?? state;
export function RunStatus({ state }: { state: string }) {
  return (
    <span className="studio-run-status rf-badge" data-state={state}>
      {runStateLabel(state)}
    </span>
  );
}
