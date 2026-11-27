import { PageHeading } from "@/components/PageHeading";
import { RunCollectionView } from "./RunCollectionView";
import { RunRecordView } from "./RunRecordView";
import { useLogbookController } from "./useLogbookController";
import "@/styles/app-workflows.css";
import "./logbook.css";
export function AppLogbookPage({
  organizationId,
  runId,
  workspaceRole,
}: {
  organizationId: string;
  runId?: string;
  workspaceRole?: string;
  selectRun?: (id?: string) => void;
}) {
  const model = useLogbookController({ organizationId, runId });
  const { current, error, loading } = model;
  if (runId && !current)
    return (
      <main className="logbook2-page">
        <PageHeading
          title="Run"
          description={
            <span role={error ? "alert" : "status"}>
              {error ||
                (loading
                  ? "Loading run…"
                  : "Run unavailable in this workspace.")}
            </span>
          }
        />
      </main>
    );
  return current ? (
    <RunRecordView
      model={model}
      current={current}
      workspaceRole={workspaceRole}
    />
  ) : (
    <RunCollectionView model={model} />
  );
}
