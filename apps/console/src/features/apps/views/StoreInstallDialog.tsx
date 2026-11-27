import { AppOperationStatus } from "@/components/AppOperationStatus";
import { StudioDialog } from "@/components/StudioDialog";
import { compatibilityMessage } from "@/lib/app-compatibility";
import type { App, Revision } from "@/lib/apps";
import { describeCapability } from "@/lib/capabilities";
import { appPath, studioPath } from "@/lib/location";
import type { AppStoreModel } from "../useAppStore";

export function StoreInstallDialog({
  model,
  app,
  revision,
}: {
  model: AppStoreModel;
  app: App;
  revision: Revision;
}) {
  const {
    organizationId,
    robot,
    robots,
    compatibility,
    existing,
    sameRelease,
    pending,
    busy,
    canInstall,
    operationId,
  } = model;
  const operation =
    operationId || (pending && existing?.candidate?.operation_id);
  return (
    <StudioDialog
      title={`Install ${app.name}`}
      onClose={model.close}
      focusHeading
    >
      <p>Release {revision.manifest.release}</p>
      {operation ? (
        <>
          <p>
            {app.name} on {robot?.name}
          </p>
          <AppOperationStatus
            operationId={operation}
            token={model.token}
            installationId={existing?.id}
            organizationId={organizationId}
            onTerminal={model.refreshAfterOperation}
          />
        </>
      ) : (
        <fieldset className="store-targets">
          <legend>Robot</legend>
          {robots.map((r) => {
            const fit = model.compatibilities[r.id];
            const installed = model.installations.some(
              (i) =>
                !i.removed_at &&
                i.robot_id === r.id &&
                i.app_id === app.id &&
                i.installed?.revision_id === revision.id,
            );
            return (
              <label key={r.id}>
                <input
                  type="radio"
                  name="install-robot"
                  checked={robot?.id === r.id}
                  disabled={busy}
                  onChange={() => model.change({ robot: r.id }, true)}
                />
                <span>
                  <strong>{r.name}</strong>
                  <small>
                    {model.statuses[r.id]?.connectivity ??
                      "Connectivity unknown"}{" "}
                    ·{" "}
                    {installed
                      ? "Installed"
                      : typeof fit === "object"
                        ? compatibilityMessage(fit.status)
                        : (fit ?? "Checking compatibility…")}
                  </small>
                </span>
              </label>
            );
          })}
        </fieldset>
      )}
      {typeof compatibility === "object" &&
        compatibility.missing_required.map((c) => (
          <p key={c.capability + c.instance_kind}>
            Required capability missing: {describeCapability(c.capability).name}
          </p>
        ))}
      {model.error && <p role="alert">{model.error}</p>}
      {existing?.candidate?.failure && (
        <p role="alert">{existing.candidate.failure.message}</p>
      )}
      {sameRelease && existing && robot ? (
        <>
          <p>Installed on {robot.name}</p>
          <a
            className="button-primary"
            href={appPath(app.id, organizationId, existing.id)}
          >
            Open on {robot.name}
          </a>
        </>
      ) : model.apps === null ? (
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
      ) : !canInstall ? (
        <p>A workspace owner or developer can install this App.</p>
      ) : existing?.installed && !pending ? (
        <a
          className="button-primary"
          href={
            appPath(app.id, organizationId, existing.id) +
            "&manage=update&release=" +
            encodeURIComponent(revision.id)
          }
        >
          Update on {robot?.name}
        </a>
      ) : (
        !operation && (
          <>
            <p>The App stays stopped after installation.</p>
            <button
              className="button-primary"
              type="button"
              disabled={
                busy ||
                !robot ||
                typeof compatibility !== "object" ||
                compatibility.status !== "compatible"
              }
              onClick={() => void model.install()}
            >
              {busy
                ? "Requesting installation…"
                : `Install on ${robot?.name ?? "robot"}`}
            </button>
          </>
        )
      )}
      {operation && existing?.candidate?.state === "failed" && (
        <button
          className="button-secondary"
          type="button"
          onClick={() => model.change({ operation: undefined }, true)}
        >
          Review robots and retry
        </button>
      )}
    </StudioDialog>
  );
}
