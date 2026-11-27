import { useRef, useState } from "react";
import { RobotArtwork, robotLooks } from "./RobotArtwork";

async function readImage(file: File): Promise<string> {
  if (!["image/png", "image/jpeg", "image/webp"].includes(file.type))
    throw new Error("Choose a PNG, JPEG or WebP image.");
  if (file.size > 10 * 1024 * 1024)
    throw new Error("Choose an image smaller than 10 MB.");
  const bitmap = await createImageBitmap(file);
  try {
    const canvas = document.createElement("canvas");
    const scale = Math.min(1, 512 / Math.max(bitmap.width, bitmap.height));
    canvas.width = Math.max(1, Math.round(bitmap.width * scale));
    canvas.height = Math.max(1, Math.round(bitmap.height * scale));
    const context = canvas.getContext("2d");
    if (!context)
      throw new Error("Image processing is unavailable in this browser.");
    context.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
    const image = canvas.toDataURL("image/webp", 0.85);
    if (atob(image.split(",")[1]).length > 256 * 1024)
      throw new Error("This image is too detailed. Choose a smaller image.");
    return image;
  } finally {
    bitmap.close();
  }
}

export function RobotAppearancePicker({
  value,
  onChange,
  onPendingChange,
}: {
  value: string | null;
  onChange: (value: string) => void;
  onPendingChange: (pending: boolean) => void;
}) {
  const [error, setError] = useState("");
  const [pending, setPending] = useState(false);
  const pendingRef = useRef(false);
  return (
    <fieldset className="robot-appearance-picker" disabled={pending}>
      <legend>Robot image or icon</legend>
      <div className="robot-appearance-options">
        {robotLooks.map(([id, label]) => (
          <label key={id}>
            <input
              type="radio"
              name="robot-appearance"
              value={id}
              checked={(value ?? "bot") === id}
              onChange={() => {
                setError("");
                onChange(id);
              }}
            />
            <RobotArtwork robot={{ appearance: id }} />
            <span>{label}</span>
          </label>
        ))}
      </div>
      <div className="flex flex-wrap items-center gap-4">
        {value?.startsWith("data:") && (
          <RobotArtwork robot={{ appearance: value }} className="h-16 w-16" />
        )}
        <label className="app-field">
          Upload an image
          <input
            type="file"
            accept="image/png,image/jpeg,image/webp"
            onChange={async (event) => {
              const file = event.target.files?.[0];
              event.target.value = "";
              if (!file || pendingRef.current) return;
              pendingRef.current = true;
              setPending(true);
              onPendingChange(true);
              setError("");
              try {
                onChange(await readImage(file));
              } catch (cause) {
                setError(
                  cause instanceof Error
                    ? cause.message
                    : "Unable to open this image.",
                );
              } finally {
                pendingRef.current = false;
                setPending(false);
                onPendingChange(false);
              }
            }}
          />
        </label>
      </div>
      <p className="text-sm text-muted-foreground">
        PNG, JPEG or WebP, up to 10 MB. Images are resized to fit.
      </p>
      {pending && <p role="status">Preparing image…</p>}
      {error && (
        <p role="alert" className="text-destructive">
          {error}
        </p>
      )}
    </fieldset>
  );
}
