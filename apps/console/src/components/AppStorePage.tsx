import { useAuth0 } from "@auth0/auth0-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import {
  type App,
  type AppImport,
  type Compatibility,
  latestRevision,
  mutateApp,
  type Revision,
  type Robot,
  readJson,
  readPage,
  type Token,
  withQuery,
} from "@/lib/apps";
import { AppOperationStatus } from "./AppOperationStatus";
import "./app-workflows.css";

const message = (error: unknown) =>
  error instanceof Error ? error.message : "Request failed";

function CapabilityList({
  label,
  items,
}: {
  label: string;
  items: { capability: string; instance_kind?: string }[];
}) {
  return (
    <div className="app-capabilities">
      <strong>{label}</strong>
      {items.length ? (
        <ul>
          {items.map((item) => (
            <li key={`${item.capability}:${item.instance_kind ?? ""}`}>
              {item.capability}
              {item.instance_kind ? ` · ${item.instance_kind}` : ""}
            </li>
          ))}
        </ul>
      ) : (
        <p>None</p>
      )}
    </div>
  );
}

export function AppStorePage({
  organizationId,
  workspaceRole,
}: {
  organizationId: string;
  workspaceRole?: string;
}) {
  const { getAccessTokenSilently } = useAuth0();
  const token = useCallback<Token>(async () => {
    const value = await getAccessTokenSilently({
      authorizationParams: { audience: import.meta.env.VITE_AUTH0_AUDIENCE },
    });
    if (!value) throw new Error("Sign in required");
    return value;
  }, [getAccessTokenSilently]);
  const [query, setQuery] = useState("");
  const [apps, setApps] = useState<App[] | null>(null);
  const [selectedId, setSelectedId] = useState("");
  const [revisions, setRevisions] = useState<Revision[]>([]);
  const [revisionId, setRevisionId] = useState("");
  const [robots, setRobots] = useState<Robot[]>([]);
  const [robotId, setRobotId] = useState("");
  const [imports, setImports] = useState<AppImport[]>([]);
  const [compatibility, setCompatibility] = useState<Compatibility | null>(
    null,
  );
  const [shareRevision, setShareRevision] = useState("");
  const [shareId, setShareId] = useState("");
  const [operationId, setOperationId] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const selectedApp = apps?.find((item) => item.id === selectedId);
  const selectedRevision = revisions.find((item) => item.id === revisionId);
  const canInstall = workspaceRole === "owner" || workspaceRole === "developer";
  const imported = useMemo(
    () => imports.some((item) => item.revision_id === revisionId),
    [imports, revisionId],
  );

  useEffect(() => {
    let active = true;
    setApps(null);
    readPage<App>(
      token,
      withQuery("/v1/store/apps", {
        organization_id: organizationId,
        q: query.trim() || undefined,
      }),
    )
      .then((items) => {
        if (active) setApps(items);
      })
      .catch((cause) => {
        if (active) setError(message(cause));
      });
    return () => {
      active = false;
    };
  }, [token, organizationId, query]);
  useEffect(() => {
    let active = true;
    Promise.all([
      readPage<Robot>(token, `/v1/organizations/${organizationId}/robots`),
      readPage<AppImport>(
        token,
        `/v1/organizations/${organizationId}/app-imports`,
      ),
    ])
      .then(([robotItems, importItems]) => {
        if (!active) return;
        setRobots(robotItems);
        setImports(importItems);
      })
      .catch((cause) => {
        if (active) setError(message(cause));
      });
    return () => {
      active = false;
    };
  }, [token, organizationId]);
  useEffect(() => {
    let active = true;
    setRevisions([]);
    setRevisionId("");
    setCompatibility(null);
    setOperationId("");
    if (!selectedId) return;
    readPage<Revision>(token, `/v1/apps/${selectedId}/revisions`)
      .then((items) => {
        if (!active) return;
        setRevisions(items);
        setRevisionId(latestRevision(items)?.id ?? "");
      })
      .catch((cause) => {
        if (active) setError(message(cause));
      });
    return () => {
      active = false;
    };
  }, [token, selectedId]);

  async function openSharedRelease(event: React.FormEvent) {
    event.preventDefault();
    setError("");
    setNotice("");
    setBusy(true);
    try {
      const revision = await readJson<Revision>(
        token,
        withQuery(`/v1/app-revisions/${shareRevision.trim()}`, {
          share_id: shareId.trim(),
        }),
      );
      const app = await readJson<App>(token, `/v1/apps/${revision.app_id}`);
      const items = await readPage<Revision>(
        token,
        `/v1/apps/${app.id}/revisions`,
      );
      setApps((current) => [
        ...(current ?? []).filter((item) => item.id !== app.id),
        app,
      ]);
      setSelectedId(app.id);
      setRevisions(items);
      setRevisionId(revision.id);
      setNotice("Shared release loaded. Check a robot before importing it.");
    } catch (cause) {
      setError(message(cause));
    } finally {
      setBusy(false);
    }
  }
  async function checkCompatibility() {
    if (!robotId || !revisionId) return;
    setError("");
    setCompatibility(null);
    setBusy(true);
    try {
      const value = await readJson<Compatibility>(
        token,
        withQuery(`/v1/robots/${robotId}/app-compatibility`, {
          revision_id: revisionId,
        }),
      );
      setCompatibility(value);
    } catch (cause) {
      setError(message(cause));
    } finally {
      setBusy(false);
    }
  }
  async function importRelease() {
    if (!revisionId) return;
    setError("");
    setNotice("");
    setBusy(true);
    try {
      const result = await mutateApp<AppImport>(
        token,
        `/v1/organizations/${organizationId}/app-imports`,
        { revision_id: revisionId, share_id: shareId.trim() || null },
      );
      setImports((current) => [
        ...(current ?? []).filter(
          (item) => item.revision_id !== result.revision_id,
        ),
        result,
      ]);
      setNotice("Release imported into this workspace.");
    } catch (cause) {
      setError(message(cause));
    } finally {
      setBusy(false);
    }
  }
  async function installRelease() {
    if (!robotId || !selectedRevision) return;
    setError("");
    setNotice("");
    setBusy(true);
    try {
      const result = await mutateApp<{
        installation_id: string;
        operation_id: string;
        run_id: null;
      }>(token, `/v1/robots/${robotId}/app-installations`, {
        revision_id: selectedRevision.id,
        share_id: shareId.trim() || null,
        configuration: selectedRevision.manifest.configuration.defaults,
      });
      setOperationId(result.operation_id);
      setNotice(
        `Installation accepted for ${robots.find((robot) => robot.id === robotId)?.name ?? "robot"}.`,
      );
    } catch (cause) {
      setError(message(cause));
    } finally {
      setBusy(false);
    }
  }

  const source =
    selectedRevision?.visibility === "listed"
      ? "Store release"
      : "Direct share";
  return (
    <main className="app-workflow-page">
      <header className="app-workflow-heading">
        <div>
          <p className="app-eyebrow">Discover and install</p>
          <h1>App Store</h1>
          <p>
            Browse published releases, check robot compatibility, and import
            them into this workspace.
          </p>
        </div>
      </header>
      {error && (
        <p className="app-feedback app-feedback-error" role="alert">
          {error}
        </p>
      )}
      {notice && (
        <p className="app-feedback" role="status">
          {notice}
        </p>
      )}
      <section className="app-store-grid">
        <div className="app-panel app-store-list">
          <label className="app-field">
            Search published Apps
            <input
              type="search"
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              placeholder="Name or app key"
            />
          </label>
          {apps === null ? (
            <p>Loading Store…</p>
          ) : apps.length ? (
            apps.map((app) => (
              <button
                type="button"
                className="app-store-row"
                aria-current={selectedId === app.id ? "true" : undefined}
                key={app.id}
                onClick={() => {
                  setSelectedId(app.id);
                  setShareId("");
                }}
              >
                <strong>{app.name}</strong>
                <span>{app.description}</span>
                <small>{app.app_key}</small>
              </button>
            ))
          ) : (
            <p>No listed Apps match this search.</p>
          )}
        </div>
        <div className="app-panel app-release-detail">
          {!selectedApp || !selectedRevision ? (
            <p>Select an App to inspect its published releases.</p>
          ) : (
            <>
              <p className="app-eyebrow">
                {source} · {selectedApp.app_key}
              </p>
              <h2>{selectedApp.name}</h2>
              <p>{selectedApp.description}</p>
              <label className="app-field">
                Release
                <select
                  value={revisionId}
                  onChange={(event) => {
                    setRevisionId(event.target.value);
                    setCompatibility(null);
                  }}
                >
                  {revisions.map((revision) => (
                    <option key={revision.id} value={revision.id}>
                      {revision.manifest.release} · {revision.visibility}
                    </option>
                  ))}
                </select>
              </label>
              <p>
                Publisher workspace{" "}
                <code>{selectedRevision.publisher_organization_id}</code>
              </p>
              <CapabilityList
                label="Required capabilities"
                items={selectedRevision.manifest.capabilities.required}
              />
              <CapabilityList
                label="Optional capabilities"
                items={selectedRevision.manifest.capabilities.optional}
              />
              <label className="app-field">
                Check against robot
                <select
                  value={robotId}
                  onChange={(event) => {
                    setRobotId(event.target.value);
                    setCompatibility(null);
                  }}
                >
                  <option value="">Choose a robot</option>
                  {robots.map((robot) => (
                    <option key={robot.id} value={robot.id}>
                      {robot.name}
                    </option>
                  ))}
                </select>
              </label>
              <div className="app-actions">
                <button
                  type="button"
                  className="button-secondary"
                  disabled={!robotId || busy}
                  onClick={() => void checkCompatibility()}
                >
                  Check compatibility
                </button>
                {canInstall && (
                  <button
                    type="button"
                    className="button-primary"
                    disabled={
                      busy ||
                      !revisionId ||
                      !robotId ||
                      !compatibility ||
                      compatibility.status !== "compatible"
                    }
                    onClick={() =>
                      void (imported ? installRelease() : importRelease())
                    }
                  >
                    {imported ? "Install on selected robot" : "Import release"}
                  </button>
                )}
              </div>
              {compatibility && (
                <div
                  className={`app-compatibility ${compatibility.status === "compatible" ? "is-ready" : "is-blocked"}`}
                  role="status"
                >
                  <strong>{compatibility.status.replaceAll("_", " ")}</strong>
                  <small>
                    Checked{" "}
                    {new Date(compatibility.checked_at).toLocaleString()}
                    {compatibility.inventory_observed_at
                      ? ` · inventory ${new Date(compatibility.inventory_observed_at).toLocaleString()}`
                      : " · no current inventory"}
                  </small>
                  {compatibility.missing_required.length > 0 && (
                    <p>
                      Missing required:{" "}
                      {compatibility.missing_required
                        .map((item) => item.capability)
                        .join(", ")}
                    </p>
                  )}
                  {compatibility.missing_native.length > 0 && (
                    <p>
                      Unavailable host requirements:{" "}
                      {compatibility.missing_native.join(", ")}
                    </p>
                  )}
                  {compatibility.missing_optional.length > 0 && (
                    <p>
                      Optional capabilities not reported:{" "}
                      {compatibility.missing_optional
                        .map((item) => item.capability)
                        .join(", ")}
                    </p>
                  )}
                </div>
              )}
              {operationId && (
                <AppOperationStatus operationId={operationId} token={token} />
              )}
            </>
          )}
        </div>
      </section>
      <details className="app-share-import">
        <summary>Import a directly shared release</summary>
        <form onSubmit={(event) => void openSharedRelease(event)}>
          <label className="app-field">
            Revision ID
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
          <button className="button-secondary" type="submit" disabled={busy}>
            Load shared release
          </button>
        </form>
      </details>
    </main>
  );
}
