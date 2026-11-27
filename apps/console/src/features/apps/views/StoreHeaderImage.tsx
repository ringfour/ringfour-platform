import type { App } from "@/lib/apps";
export function isCameraSnapshotLoop(app: Pick<App, "app_key" | "name">) {
  return (
    app.app_key === "camera-snapshot-loop" ||
    app.name.toLowerCase() === "camera snapshot loop"
  );
}

export function StoreHeaderImage({
  app,
  priority = false,
}: {
  app: App;
  priority?: boolean;
}) {
  const isCapture = hasCameraPreview(app);
  const isNote = /(?:artifact|evidence)-note/.test(app.app_key);
  const image = isCapture
    ? "camera-capture.png"
    : isNote
      ? "store-notebook.jpg"
      : "store-robot.jpg";

  return (
    <img
      className="store-header-image"
      src={`/app-artwork/${image}`}
      alt={
        isCapture
          ? "A red obstacle seen from the simulated robot’s base camera"
          : ""
      }
      loading={priority ? "eager" : "lazy"}
      decoding="async"
    />
  );
}

/** The retained sample belongs to the official demo and its original local publication. */
export function hasCameraPreview(app: App) {
  return (
    isCameraSnapshotLoop(app) &&
    (app.publisher_type === "ringfour" ||
      app.id === "188ee7c7-6499-492b-931c-b97a479dce06")
  );
}
