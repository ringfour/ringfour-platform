import { ArrowRight } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { compatibilityMessage } from "@/lib/app-compatibility";
import {
  type App,
  type Compatibility,
  type Installation,
  latestRevision,
  type Revision,
  type Robot,
  readJson,
  readPage,
  type Token,
  withQuery,
} from "@/lib/apps";
import { studioPath } from "@/lib/location";
import { AppArtwork } from "./AppArtwork";
import { Publisher } from "./Publisher";
import { hasCameraPreview, StoreHeaderImage } from "./StoreHeaderImage";

function CatalogRobotFit({
  app,
  robot,
  token,
}: {
  app: App;
  robot: Robot;
  token: Token;
}) {
  const [label, setLabel] = useState("Checking compatibility…");
  useEffect(() => {
    let live = true;
    async function load() {
      const revision = latestRevision(
        (
          await readPage<Revision>(token, `/v1/apps/${app.id}/revisions`)
        ).filter((r) => r.visibility === "listed"),
      )?.id;
      if (!revision) throw new Error();
      const fit = await readJson<Compatibility>(
        token,
        withQuery(`/v1/robots/${robot.id}/app-compatibility`, {
          revision_id: revision,
        }),
      );
      if (live)
        setLabel(
          fit.status === "compatible"
            ? `Works with ${robot.name}`
            : compatibilityMessage(fit.status),
        );
    }
    void load().catch(() => {
      if (live) setLabel("Compatibility unavailable");
    });
    return () => {
      live = false;
    };
  }, [app, robot, token]);
  return <span>{label}</span>;
}

export function StoreCatalogView({
  apps,
  workspace,
  robot,
  installations,
  query,
  token,
  clear,
}: {
  apps: App[];
  workspace: string;
  robot?: Robot;
  installations: Installation[];
  query: string;
  token: Token;
  clear: () => void;
}) {
  const term = query.trim().toLowerCase();
  const visible = apps.filter((app) =>
    [
      app.name,
      app.description,
      app.publisher_name,
      app.publisher_type === "ringfour" ? "Ringfour official" : "",
    ]
      .join(" ")
      .toLowerCase()
      .includes(term),
  );
  const featured = !term && (visible.find(hasCameraPreview) ?? visible[0]);
  const more = visible.filter((app) => app !== featured);
  const root = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const id = window.history.state?.storeFocus;
    if (id)
      Array.from(root.current?.querySelectorAll("a") ?? [])
        .find((a) => a.dataset.app === id)
        ?.focus({ preventScroll: true });
  }, []);
  function card(app: App, feature = false) {
    const installed = installations.filter(
      (i) =>
        i.app_id === app.id &&
        i.installed &&
        !i.removed_at &&
        (!robot || i.robot_id === robot.id),
    );
    const sample = hasCameraPreview(app);
    return (
      <a
        key={app.id}
        className="store-listing-card"
        data-app={app.id}
        data-featured={feature || undefined}
        aria-label={`View ${app.name}`}
        onClick={() =>
          window.history.replaceState(
            { ...window.history.state, storeFocus: app.id },
            "",
          )
        }
        href={studioPath("store", workspace, app.id, {
          robot: robot?.id,
        })}
      >
        <div className="store-card-media">
          <StoreHeaderImage app={app} priority={feature} />
          {sample && (
            <span className="store-sample-caption">
              Sample capture · Base101 simulator
            </span>
          )}
        </div>
        <div className="store-card-body">
          <div className="store-card-identity">
            <AppArtwork app={app} className="h-12 w-12" />
            <div>
              {!feature && <h3 className="store-card-title">{app.name}</h3>}
              <Publisher app={app} />
            </div>
          </div>
          {feature && <h2 className="store-card-title">{app.name}</h2>}
          <p className="store-card-description">{app.description}</p>
          <div className="store-card-footer">
            {installed.length ? (
              <span>
                {robot
                  ? `Installed on ${robot.name}`
                  : `Installed on ${installed.length} robot${installed.length === 1 ? "" : "s"}`}
              </span>
            ) : (
              robot && (
                <CatalogRobotFit
                  key={robot.id}
                  app={app}
                  robot={robot}
                  token={token}
                />
              )
            )}
            <span className="store-card-link">
              View App <ArrowRight size={16} aria-hidden="true" />
            </span>
          </div>
        </div>
      </a>
    );
  }
  return (
    <div ref={root} className="store-editorial">
      {featured && card(featured, true)}
      {more.length > 0 && (
        <section>
          <h2>{term || apps.length > 5 ? "Browse Apps" : "More Apps"}</h2>
          <div className="store-card-grid">{more.map((app) => card(app))}</div>
        </section>
      )}
      {!visible.length && (
        <div>
          <p>No Apps match “{query}”.</p>
          <button className="button-secondary" type="button" onClick={clear}>
            Clear search
          </button>
        </div>
      )}
    </div>
  );
}
