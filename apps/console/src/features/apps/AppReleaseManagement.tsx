import { useCallback, useEffect, useRef, useState } from "react";
import { AppOperationStatus } from "@/components/AppOperationStatus";
import { SchemaFields } from "@/components/forms/SchemaFields";
import { StudioDialog } from "@/components/StudioDialog";
import {
  type App,
  type AppConfiguration,
  type AppRun,
  type Compatibility,
  type Installation,
  latestRevision,
  mutateApp,
  type Revision,
  type Robot,
  readJson,
  readPage,
  type Token,
} from "@/lib/apps";
import { replaceQuery, setQuery, useLocation } from "@/lib/location";
import type { RobotAppStatus } from "@/lib/robot-status";
import { StudioSection } from "@/views/StudioPage";

type Values = Record<string, string | number | boolean>;
export function AppReleaseManagement({
  app,
  installation,
  robot,
  revisions,
  runs,
  token,
  canManage,
  onChanged,
  onPendingChange,
}: {
  app: App;
  installation: Installation;
  robot?: Robot;
  revisions: Revision[];
  runs: AppRun[];
  token: Token;
  canManage: boolean;
  onChanged: () => void;
  onPendingChange?: (pending: boolean) => void;
}) {
  const location = useLocation();
  const kind = location.searchParams.get("manage") ?? "";
  const operationId = location.searchParams.get("operation") ?? "";
  const binding = installation.installed;
  const [configurations, setConfigurations] = useState<AppConfiguration[]>([]);
  const [status, setStatus] = useState<RobotAppStatus | null>(null);
  const [revisionId, setRevisionId] = useState("");
  const [configurationId, setConfigurationId] = useState("");
  const [values, setValues] = useState<Values>({});
  const [runtime, setRuntime] = useState<Values>({});
  const [compatibility, setCompatibility] = useState<Compatibility | null>(
    null,
  );
  const [error, setError] = useState("");
  const [shareId, setShareId] = useState("");
  const [busy, setBusy] = useState(false);
  const [pending, setPending] = useState(
    Boolean(
      operationId ||
        (installation.candidate && installation.candidate.state !== "failed"),
    ),
  );
  useEffect(() => {
    if (operationId) setPending(true);
  }, [operationId]);
  useEffect(() => {
    onPendingChange?.(pending);
  }, [pending, onPendingChange]);
  const current = revisions.find((item) => item.id === binding?.revision_id);
  const latest = latestRevision(revisions);
  const target = revisions.find((item) => item.id === revisionId);
  const active = runs.some(
    (run) =>
      run.robot_id === installation.robot_id &&
      ["accepted", "running"].includes(run.state),
  );
  const previous = configurations.filter(
    (item) => item.id !== binding?.configuration_revision_id,
  );
  const eligible = configurations.find((item) => item.id === configurationId);
  const terminal = useCallback(() => {
    setPending(false);
    onChanged();
  }, [onChanged]);
  useEffect(() => {
    void binding?.configuration_revision_id;
    let live = true;
    setError("");
    void readJson<RobotAppStatus>(
      token,
      `/v1/robots/${installation.robot_id}/app-status`,
    )
      .then((value) => {
        if (live) setStatus(value);
      })
      .catch((cause) => {
        if (live) setError(String(cause));
      });
    void readPage<AppConfiguration>(
      token,
      `/v1/app-installations/${installation.id}/configuration-revisions`,
    )
      .then((items) => {
        if (live) setConfigurations(items);
      })
      .catch((cause) => {
        if (live)
          setError(
            cause instanceof Error
              ? cause.message
              : "Retained release history is unavailable.",
          );
      });
    return () => {
      live = false;
    };
  }, [
    installation.id,
    installation.robot_id,
    binding?.configuration_revision_id,
    token,
  ]);
  const requestedRevision = location.searchParams.get("release");
  const initializedReview = useRef("");
  useEffect(() => {
    if (!["update", "rollback"].includes(kind)) {
      initializedReview.current = "";
      return;
    }
    const key = kind + ":" + installation.id + ":" + requestedRevision;
    if (initializedReview.current === key) return;
    const previous = configurations.filter(
      (item) => item.id !== binding?.configuration_revision_id,
    );
    const selected =
      kind === "rollback"
        ? previous[previous.length - 1]?.revision_id
        : (requestedRevision ?? latest?.id);
    if (!selected) return;
    initializedReview.current = key;
    setRevisionId(selected);
    setConfigurationId(previous[previous.length - 1]?.id ?? "");
  }, [
    kind,
    installation.id,
    binding?.configuration_revision_id,
    configurations,
    requestedRevision,
    latest,
  ]);
  useEffect(() => {
    let live = true;
    setCompatibility(null);
    if (!target) return;
    setValues({ ...target.manifest.configuration.defaults });
    setRuntime({ ...target.manifest.runtime_settings?.defaults });
    void readJson<Compatibility>(
      token,
      `/v1/robots/${installation.robot_id}/app-compatibility?revision_id=${target.id}`,
    )
      .then((value) => {
        if (live) setCompatibility(value);
      })
      .catch((cause) => {
        if (live)
          setError(
            cause instanceof Error
              ? cause.message
              : "Compatibility unavailable",
          );
      });
    return () => {
      live = false;
    };
  }, [target, installation.robot_id, token]);
  async function apply(event: React.FormEvent) {
    event.preventDefault();
    if (!binding || !canManage || busy || active || pending) return;
    setBusy(true);
    setError("");
    try {
      const body = {
        expected_revision_id: binding.revision_id,
        expected_configuration_revision_id: binding.configuration_revision_id,
        ...(kind === "update"
          ? {
              revision_id: target?.id,
              share_id: shareId.trim() || null,
              configuration: values,
              runtime_settings: target?.manifest.runtime_settings
                ? runtime
                : null,
            }
          : kind === "rollback"
            ? {
                revision_id: eligible?.revision_id,
                configuration_revision_id: eligible?.id,
              }
            : {}),
      };
      const accepted = await mutateApp<{ operation_id: string }>(
        token,
        `/v1/app-installations/${installation.id}/${kind}`,
        body,
      );
      setPending(true);
      replaceQuery({ operation: accepted.operation_id, manage: undefined });
      onChanged();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Change failed");
    } finally {
      setBusy(false);
    }
  }
  return (
    <StudioSection
      title="Installation"
      titleId="installation-management-title"
      className="studio-management-section"
      description={
        binding
          ? "Manage the installed release, rollback or remove this installation."
          : "Installation removed"
      }
    >
      <div className="studio-form">
        {latest && latest.id !== binding?.revision_id && (
          <p>Available release {latest.manifest.release}</p>
        )}
        {error && (
          <p role="alert" className="app-feedback app-feedback-error">
            {error}
          </p>
        )}
        {operationId && (
          <AppOperationStatus
            key={operationId}
            token={token}
            operationId={operationId}
            installationId={installation.id}
            organizationId={installation.organization_id}
            onTerminal={terminal}
          />
        )}
        <div className="app-actions">
          <button
            className="button-secondary"
            type="button"
            disabled={!canManage || !binding}
            onClick={() => setQuery("manage", "update")}
          >
            Review update
          </button>
          <button
            className="button-secondary"
            type="button"
            disabled={!canManage || !binding}
            onClick={() => setQuery("manage", "rollback")}
          >
            Rollback
          </button>
          <button
            className="button-danger"
            type="button"
            disabled={
              !canManage || !binding || status?.supports_app_uninstall !== true
            }
            onClick={() => setQuery("manage", "uninstall")}
          >
            Uninstall
          </button>
        </div>
        {status?.supports_app_uninstall !== true && binding && (
          <p className="app-muted">
            Uninstall requires a connected robot with removal support confirmed
            by its current runtime.
          </p>
        )}
        {["update", "rollback", "uninstall"].includes(kind) && (
          <StudioDialog
            title={`${kind === "update" ? "Update" : kind === "rollback" ? "Rollback" : "Uninstall"} ${app.name} on ${robot?.name ?? "robot"}`}
            onClose={() => setQuery("manage", "")}
          >
            <form
              onSubmit={(event) => void apply(event)}
              className="robot-access-form"
            >
              <p>
                Installed release {current?.manifest.release ?? "unavailable"}
              </p>
              {active && (
                <p role="alert">
                  Stop the active App on this robot before changing its
                  installation.
                </p>
              )}
              {pending && (
                <p role="status">
                  An installation change is awaiting device confirmation
                </p>
              )}
              {kind === "uninstall" ? (
                <p>
                  Remove {app.name} from {robot?.name ?? "this robot"}.
                  Published files, run history and the workspace library are
                  retained. Other robots keep their installations.
                </p>
              ) : kind === "rollback" ? (
                <>
                  {previous.length ? (
                    <label className="app-field">
                      Previously applied configuration
                      <select
                        value={configurationId}
                        onChange={(event) => {
                          setConfigurationId(event.target.value);
                          setRevisionId(
                            configurations.find(
                              (item) => item.id === event.target.value,
                            )?.revision_id ?? "",
                          );
                        }}
                      >
                        {previous.map((item) => (
                          <option key={item.id} value={item.id}>
                            Release{" "}
                            {revisions.find(
                              (revision) => revision.id === item.revision_id,
                            )?.manifest.release ?? "unavailable"}{" "}
                            · {item.id.slice(0, 8)}
                          </option>
                        ))}
                      </select>
                    </label>
                  ) : (
                    <p>
                      No previous applied configuration is available to roll
                      back to.
                    </p>
                  )}
                  {eligible && (
                    <details>
                      <summary>Retained startup values</summary>
                      <pre>{JSON.stringify(eligible.values, null, 2)}</pre>
                    </details>
                  )}
                </>
              ) : (
                <>
                  <label className="app-field">
                    Available release
                    <select
                      value={revisionId}
                      onChange={(event) => setRevisionId(event.target.value)}
                    >
                      {revisions.map((item) => (
                        <option key={item.id} value={item.id}>
                          {item.manifest.release}
                          {item.id === binding?.revision_id
                            ? " · Installed"
                            : ""}
                        </option>
                      ))}
                    </select>
                  </label>
                  {target && (
                    <>
                      {target.visibility === "unlisted" && (
                        <details>
                          <summary>Shared release authorization</summary>
                          <label className="app-field">
                            Share ID (if required)
                            <input
                              value={shareId}
                              onChange={(event) =>
                                setShareId(event.target.value)
                              }
                            />
                          </label>
                        </details>
                      )}
                      <h3>Startup configuration for this release</h3>
                      <p>
                        Review these values before applying the update. The App
                        remains stopped.
                      </p>
                      <SchemaFields
                        schema={target.manifest.configuration.schema}
                        values={values}
                        onChange={setValues}
                        prefix="update"
                      />
                      {target.images?.map((image) => (
                        <p key={image.platform}>
                          {image.platform} ·{" "}
                          {new Intl.NumberFormat(undefined, {
                            style: "unit",
                            unit: "megabyte",
                            maximumFractionDigits: 1,
                          }).format(image.size_bytes / 1_000_000)}
                        </p>
                      ))}
                      {target.manifest.runtime_settings && (
                        <>
                          <h3>Live settings for this release</h3>
                          <SchemaFields
                            schema={target.manifest.runtime_settings.schema}
                            values={runtime}
                            onChange={setRuntime}
                            prefix="runtime"
                          />
                        </>
                      )}
                      <h3>Requirements</h3>
                      <p>
                        Required:{" "}
                        {target.manifest.capabilities.required
                          .map(
                            (item) =>
                              item.capability +
                              (item.instance_kind
                                ? ` (${item.instance_kind})`
                                : ""),
                          )
                          .join(", ") || "None"}
                      </p>
                      <p>
                        Optional:{" "}
                        {target.manifest.capabilities.optional
                          .map((item) => item.capability)
                          .join(", ") || "None"}
                      </p>
                      {current &&
                        JSON.stringify(current.manifest.capabilities) !==
                          JSON.stringify(target.manifest.capabilities) && (
                          <p>
                            Capability requirements have changed since the
                            installed release.
                          </p>
                        )}
                    </>
                  )}
                </>
              )}
              {kind !== "uninstall" && (
                <p role="status">
                  {compatibility
                    ? compatibility.status === "compatible"
                      ? "Robot meets this release’s requirements"
                      : `Compatibility: ${compatibility.status.replaceAll("_", " ")}`
                    : "Checking compatibility…"}
                </p>
              )}
              <p>
                Changes are confirmed by the robot. A failed update keeps the
                prior installed release usable.
              </p>
              <button
                className={
                  kind === "uninstall" ? "button-danger" : "button-primary"
                }
                type="submit"
                disabled={
                  !canManage ||
                  busy ||
                  active ||
                  pending ||
                  !binding ||
                  (kind === "uninstall"
                    ? status?.supports_app_uninstall !== true
                    : compatibility?.status !== "compatible" ||
                      (kind === "rollback"
                        ? !eligible
                        : !target || target.id === binding.revision_id))
                }
              >
                {busy
                  ? "Requesting change…"
                  : kind === "uninstall"
                    ? `Uninstall from ${robot?.name ?? "robot"}`
                    : kind === "rollback"
                      ? "Apply rollback"
                      : "Apply update"}
              </button>
            </form>
          </StudioDialog>
        )}
      </div>
    </StudioSection>
  );
}
