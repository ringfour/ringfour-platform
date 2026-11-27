import { AppOperationStatus } from "@/components/AppOperationStatus";
import { PageHeading } from "@/components/PageHeading";
import { AppFiles } from "@/features/files/AppFiles";
import { appPath } from "@/lib/location";
import { AppActivity } from "./AppActivity";
import { AppExecution } from "./AppExecution";
import { AppLiveSettings } from "./AppLiveSettings";
import { AppOverview } from "./AppOverview";
import { AppReleaseManagement } from "./AppReleaseManagement";
import { AppStartupConfiguration } from "./AppStartupConfiguration";
import type { AppsScope } from "./model";
import { useAppsController } from "./useAppsController";
import { AppDetailFrame } from "./views/AppDetailFrame";
import { InstalledAppsView } from "./views/InstalledAppsView";
import "@/styles/app-workflows.css";
export function AppsPage(props: AppsScope) {
  const { organizationId, focusAppId } = props;
  const {
    token,
    operation,
    error,
    loading,
    changing,
    snapshot: {
      apps,
      robots,
      installations,
      runs,
      revisionChoices,
      configurationValues,
    },
    selection: {
      robotFilter,
      query,
      focusedApp,
      focusedImport,
      focusedInstallations,
      selectedInstallationId,
      selectedInstallation,
      focusedRobot,
      detail,
      focusedSection,
    },
    permissions: { canManage, canOperate },
    actions: {
      refresh: refreshAfterOperation,
      run: update,
      setChanging,
      markLiveDirty: setLiveDirty,
    },
    startup,
  } = useAppsController(props);
  const {
    editing,
    dirty: startupDirty,
    error: startupError,
    activeFor,
    edit: editConfiguration,
  } = startup;
  if (focusAppId && loading)
    return (
      <main className="app-workflow-page">
        <PageHeading
          title="Opening App…"
          description={<span role="status">Loading App details</span>}
        />
      </main>
    );
  if (focusAppId && (!focusedImport || !focusedApp))
    return (
      <main className="app-workflow-page">
        <PageHeading
          title="App details could not be opened"
          description={
            error || "This App may not be imported into the selected workspace."
          }
          actions={
            <button
              className="button-secondary"
              type="button"
              onClick={refreshAfterOperation}
            >
              Try again
            </button>
          }
        />
      </main>
    );
  if (selectedInstallationId && !selectedInstallation)
    return (
      <main className="app-workflow-page">
        <PageHeading
          title="Installation unavailable"
          description="This installation is unavailable in the selected workspace."
          actions={
            <a
              className="button-secondary"
              href={appPath(focusAppId ?? "", organizationId)}
            >
              Choose a current robot installation →
            </a>
          }
        />
      </main>
    );
  if (!detail || !focusedApp)
    return (
      <>
        {operation && (
          <AppOperationStatus
            operationId={operation}
            token={token}
            onTerminal={refreshAfterOperation}
          />
        )}
        <InstalledAppsView
          workspace={organizationId}
          apps={apps}
          robots={robots}
          installations={installations}
          revisionChoices={revisionChoices}
          runs={runs}
          robotFilter={robotFilter}
          query={query}
          loading={loading}
          error={error}
          onRetry={refreshAfterOperation}
        />
      </>
    );
  return (
    <AppDetailFrame
      app={focusedApp}
      workspace={organizationId}
      installations={focusedInstallations}
      robots={robots}
      selected={selectedInstallation}
      section={focusedSection}
      revision={revisionChoices[focusAppId ?? ""]?.find(
        (item) =>
          item.id ===
          (selectedInstallation
            ? (selectedInstallation.installed?.revision_id ??
              selectedInstallation.candidate?.revision_id)
            : focusedImport?.revision_id),
      )}
      execution={
        (selectedInstallation || focusedSection === "overview") && (
          <AppExecution
            key={`${selectedInstallation?.id ?? "choose-target"}:${selectedInstallation?.installed?.revision_id ?? ""}`}
            app={focusedApp}
            organizationId={organizationId}
            installation={selectedInstallation}
            installationCount={focusedInstallations.length}
            robot={focusedRobot}
            runs={runs}
            apps={apps}
            token={token}
            canOperate={canOperate}
            pendingOperation={
              changing ||
              !!(
                selectedInstallation?.candidate &&
                selectedInstallation.candidate.state !== "failed"
              )
            }
            onAction={(kind) => {
              if (selectedInstallation) void update(selectedInstallation, kind);
            }}
          />
        )
      }
      operation={operation}
    >
      {error && (
        <p role="alert" className="app-feedback app-feedback-error">
          {error}
        </p>
      )}
      {operation &&
        focusedSection !== "settings" &&
        !(detail && focusedSection === "overview" && selectedInstallation) && (
          <AppOperationStatus
            operationId={operation}
            token={token}
            onTerminal={refreshAfterOperation}
          />
        )}
      {focusedSection === "overview" && (
        <AppOverview
          app={focusedApp}
          organizationId={organizationId}
          installation={selectedInstallation}
          runs={runs}
          token={token}
        />
      )}
      {detail &&
        focusedSection === "overview" &&
        focusedApp &&
        selectedInstallation && (
          <AppReleaseManagement
            key={selectedInstallation.id}
            app={focusedApp}
            installation={selectedInstallation}
            robot={focusedRobot}
            revisions={revisionChoices[focusAppId ?? ""] ?? []}
            runs={runs}
            token={token}
            canManage={canManage}
            onChanged={refreshAfterOperation}
            onPendingChange={setChanging}
          />
        )}
      {focusedSection === "settings" && (
        <AppStartupConfiguration
          installation={selectedInstallation}
          revision={revisionChoices[focusAppId ?? ""]?.find(
            (item) => item.id === selectedInstallation?.installed?.revision_id,
          )}
          values={configurationValues[selectedInstallation?.id ?? ""] ?? {}}
          editing={
            editing?.installation.id === selectedInstallation?.id
              ? editing
              : null
          }
          dirty={startupDirty}
          active={!!selectedInstallation && activeFor(selectedInstallation)}
          canManage={canManage}
          error={startupError}
          onEdit={() => {
            if (selectedInstallation)
              void editConfiguration(selectedInstallation);
          }}
          onChange={startup.change}
          onSave={(event) => void startup.save(event)}
          onCancel={startup.cancel}
          feedback={
            operation ? (
              <AppOperationStatus
                operationId={operation}
                token={token}
                installationId={selectedInstallation?.id}
                organizationId={organizationId}
                onTerminal={refreshAfterOperation}
              />
            ) : undefined
          }
        />
      )}
      {detail && focusedSection === "settings" && selectedInstallation && (
        <AppLiveSettings
          key={selectedInstallation.id}
          installationId={selectedInstallation.id}
          organizationId={organizationId}
          robotName={focusedRobot?.name ?? "Robot"}
          token={token}
          canManage={canManage}
          onDirtyChange={setLiveDirty}
        />
      )}
      {detail && focusedSection === "files" && (
        <AppFiles
          key={`${focusAppId ?? ""}:${selectedInstallation?.id ?? "all"}`}
          appId={focusAppId ?? ""}
          organizationId={organizationId}
          installationId={selectedInstallation?.id}
          robotName={focusedRobot?.name}
          robots={robots}
          runs={runs.filter((run) => run.snapshot.app_id === focusAppId)}
          token={token}
        />
      )}
      {detail &&
        focusedSection === "activity" &&
        (selectedInstallation ? (
          <AppActivity
            key={`${focusAppId ?? ""}:${selectedInstallation?.id ?? "all"}`}
            appId={focusAppId ?? ""}
            organizationId={organizationId}
            robots={robots}
            runs={runs.filter((run) => run.snapshot.app_id === focusAppId)}
            installationId={selectedInstallation.id}
            token={token}
          />
        ) : (
          <p className="app-muted">
            Choose a robot to see activity for its installation.
          </p>
        ))}
    </AppDetailFrame>
  );
}
