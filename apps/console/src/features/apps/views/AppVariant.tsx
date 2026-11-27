import type { App } from "@/lib/apps";

/** Disambiguate similar names using the publisher credit. */
export function AppVariant({ app, peers }: { app: App; peers: App[] }) {
  const comparableName = (name: string) =>
    name
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, " ")
      .trim();
  const matches = peers.filter((other) => {
    const name = comparableName(app.name);
    const otherName = comparableName(other.name);
    return (
      other.id !== app.id &&
      (name === otherName ||
        name.startsWith(otherName + " ") ||
        otherName.startsWith(name + " "))
    );
  });
  if (!matches.length) return null;
  const publisher =
    app.publisher_name ??
    (app.publisher_type === "ringfour" ? "Ringfour" : "Workspace publisher");
  return (
    <span className="studio-app-variant" title={app.app_key}>
      {publisher}
    </span>
  );
}
