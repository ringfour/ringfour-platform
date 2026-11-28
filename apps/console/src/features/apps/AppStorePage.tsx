import { AppOperationStatus } from "@/components/AppOperationStatus";
import { PageHeading } from "@/components/PageHeading";
import { appPath, studioPath } from "@/lib/location";
import { StudioPage, StudioSection } from "@/views/StudioPage";
import { useAppStore } from "./useAppStore";
import { AppArtwork } from "./views/AppArtwork";
import { AppRequirements } from "./views/AppRequirements";
import { Publisher } from "./views/Publisher";
import { StoreCatalogView } from "./views/StoreCatalogView";
import { hasCameraPreview, StoreHeaderImage } from "./views/StoreHeaderImage";
import { StoreInstallDialog } from "./views/StoreInstallDialog";
import { StoreRobotFit } from "./views/StoreRobotFit";
import "@/styles/app-workflows.css";
export function AppStorePage(props: {
  organizationId: string;
  workspaceName?: string;
  workspaceRole?: string;
}) {
  const model = useAppStore(props);
  const {
    organizationId,
    token,
    selectedId,
    requestedRobot,
    modal,
    operationId,
    query,
    apps,
    selectedApp,
    revisions,
    robots,
    existing,
    selectedRevision,
    installations,
    shareRevision,
    shareId,
    busy,
    error,
    robot,
    sameRelease,
    pending,
    change,
    shared,
    refreshAfterOperation,

    setShareRevision,
    setShareId,
    openInstall,
  } = model;
  const feedback = error && (
    <p role="alert" className="app-feedback app-feedback-error">
      {error}{" "}
      <button type="button" className="button-secondary" onClick={model.reload}>
        Retry
      </button>
    </p>
  );
  if (!selectedId)
    return (
      <StudioPage
        className="store-page"
        heading={
          <PageHeading
            title="Hub"
            description="Browse Apps to install on your robots."
            actions={
              <label className="app-field">
                Robot
                <select
                  value={requestedRobot ?? ""}
                  onChange={(e) => change({ robot: e.target.value }, true)}
                >
                  <option value="">All robots</option>
                  {robots.map((r) => (
                    <option key={r.id} value={r.id}>
                      For {r.name}
                    </option>
                  ))}
                </select>
              </label>
            }
          />
        }
      >
        {(query || (apps?.length ?? 0) > 5) && (
          <label className="app-field store-search">
            Search Apps
            <input
              type="search"
              value={query}
              onChange={(e) => change({ q: e.target.value }, true)}
            />
          </label>
        )}
        {feedback}
        <div className="studio-sections">
          {apps === null ? (
            <p className="store-loading" role="status">
              {error ? "Hub unavailable" : "Loading Hub…"}
            </p>
          ) : !apps.length ? (
            <p>No Apps are available to this workspace yet.</p>
          ) : (
            <StoreCatalogView
              apps={apps}
              workspace={organizationId}
              robot={requestedRobot ? robot : undefined}
              token={token}
              clear={() => change({ q: undefined }, true)}
              installations={installations}
              query={query}
            />
          )}
          {model.canInstall && (
            <details>
              <summary>Import a shared App</summary>
              <form className="robot-access-form" onSubmit={shared}>
                <label className="app-field">
                  Release ID
                  <input
                    required
                    value={shareRevision}
                    onChange={(event) => setShareRevision(event.target.value)}
                  />
                </label>
                <label className="app-field">
                  Share ID
                  <input
                    required
                    value={shareId}
                    onChange={(event) => setShareId(event.target.value)}
                  />
                </label>
                <button
                  className="button-secondary"
                  disabled={busy}
                  type="submit"
                >
                  Import shared App
                </button>
              </form>
            </details>
          )}
        </div>
      </StudioPage>
    );

  if (!selectedApp || !selectedRevision)
    return (
      <StudioPage
        className="store-page"
        heading={
          <PageHeading
            title={
              selectedApp?.name ?? (error ? "App unavailable" : "Opening App…")
            }
            description={
              selectedApp && (
                <span role="status">
                  {apps !== null
                    ? "This release is unavailable to this workspace."
                    : "Loading releases…"}
                </span>
              )
            }
          />
        }
      >
        {feedback}
      </StudioPage>
    );

  const showCapture = hasCameraPreview(selectedApp);
  const fit = model.compatibility;
  const eligible = typeof fit === "object" && fit.status === "compatible";
  const installedVersion = revisions.find(
    (r) => r.id === existing?.installed?.revision_id,
  )?.manifest.release;
  const updateHref =
    existing &&
    appPath(selectedId, organizationId, existing.id) +
      "&manage=update&release=" +
      encodeURIComponent(selectedRevision.id);
  const op = operationId || (pending && existing?.candidate?.operation_id);
  const status = robot && model.statuses[robot.id];
  return (
    <StudioPage
      className="store-page"
      heading={
        <PageHeading
          className="store-detail-header"
          title={selectedRevision.manifest.display.name || selectedApp.name}
          metadata={
            <>
              <AppArtwork app={selectedApp} className="h-16 w-16" />
              <Publisher app={selectedApp} />
            </>
          }
          description={selectedRevision.manifest.display.description}
          actions={
            <div className="store-install-area">
              {apps === null ? (
                <p>Loading robots…</p>
              ) : !robots.length ? (
                <a
                  className="button-primary"
                  href={studioPath("robots", organizationId, "enroll", {
                    return_to: model.location.pathname + model.location.search,
                  })}
                >
                  Enroll a robot
                </a>
              ) : pending ? (
                <p role="status">Installation in progress on {robot?.name}</p>
              ) : sameRelease && robot ? (
                <a
                  className="button-primary"
                  href={appPath(selectedId, organizationId, existing?.id)}
                >
                  Open on {robot.name}
                </a>
              ) : !model.canInstall ? (
                <p>A workspace owner or developer can install this App.</p>
              ) : existing?.installed && updateHref ? (
                <>
                  <p>
                    Installed {installedVersion ?? "release"} · Selected{" "}
                    {selectedRevision.manifest.release}
                  </p>
                  <a className="button-primary" href={updateHref}>
                    Update on {robot?.name}
                  </a>
                </>
              ) : (
                <button
                  className="button-primary"
                  type="button"
                  onClick={openInstall}
                  disabled={!!robot && !eligible}
                >
                  {robot ? `Install on ${robot.name}` : "Choose robot"}
                </button>
              )}
              {robot && (
                <StoreRobotFit
                  key={selectedRevision.id + robot.id}
                  robot={robot}
                  token={token}
                  compatibility={fit}
                  camera={showCapture}
                  reportedStatus={status}
                />
              )}
              {status?.connectivity === "offline" && (
                <p>Robot offline · installation waits for reconnection.</p>
              )}
              {robots.length > 1 && (robot || !model.canInstall) && (
                <button
                  type="button"
                  className="button-secondary"
                  onClick={openInstall}
                >
                  {robot ? "Choose another robot" : "Choose robot"}
                </button>
              )}
              {op && !modal && (
                <AppOperationStatus
                  key={op}
                  operationId={op}
                  organizationId={organizationId}
                  installationId={existing?.id}
                  token={token}
                  onTerminal={refreshAfterOperation}
                />
              )}
              {existing?.candidate?.failure && (
                <p role="alert">{existing.candidate.failure.message}</p>
              )}
            </div>
          }
        />
      }
    >
      {feedback}
      {showCapture && (
        <figure className="store-capture">
          <StoreHeaderImage app={selectedApp} priority />
          <figcaption>Sample capture · Base101 simulator</figcaption>
        </figure>
      )}
      <div className="studio-split store-information">
        <div className="studio-sections">
          {showCapture && (
            <StudioSection title="About this App" titleId="store-about">
              <p>
                Start the App to take pictures at regular intervals with the
                robot’s camera. Open Files on the installation to see the images
                from your run.
              </p>
            </StudioSection>
          )}
          <StudioSection title="Release" titleId="store-release-heading">
            <p>
              {selectedRevision.manifest.release} · Published{" "}
              {new Date(selectedRevision.published_at).toLocaleDateString()} ·{" "}
              {selectedRevision.visibility === "listed"
                ? "Public release"
                : `Private release · available in ${props.workspaceName ?? "this workspace"}`}
            </p>
            {revisions.length > 1 && (
              <label className="app-field store-search">
                Release
                <select
                  value={selectedRevision.id}
                  onChange={(event) =>
                    change(
                      { release: event.target.value, operation: undefined },
                      true,
                    )
                  }
                >
                  {revisions.map((item) => (
                    <option key={item.id} value={item.id}>
                      {item.manifest.release}
                    </option>
                  ))}
                </select>
              </label>
            )}
            <details>
              <summary>Release details</summary>
              <code className="break-all">
                {selectedRevision.manifest_digest}
              </code>
            </details>
          </StudioSection>
        </div>
        <StudioSection
          title="Robot requirements"
          titleId="store-requirements-heading"
        >
          <AppRequirements
            revision={selectedRevision}
            robot={robot}
            compatibility={fit}
            inventory={status?.inventory?.current}
          />
          {robot && (
            <a
              className="store-card-link"
              href={
                studioPath("robots", organizationId, robot.id) +
                "#capabilities-heading"
              }
            >
              View {robot.name} capabilities
            </a>
          )}
        </StudioSection>
      </div>
      {modal && (
        <StoreInstallDialog
          model={model}
          app={selectedApp}
          revision={selectedRevision}
        />
      )}
    </StudioPage>
  );
}
