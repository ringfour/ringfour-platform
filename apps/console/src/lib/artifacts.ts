import { apiFetch } from "./api";
import type { Token } from "./apps";
export type ArtifactReceipt = {
  artifact_id: string;
  run_id: string;
  path: string;
  size: number;
  sha256: string;
  content_type: string;
};
export function isPreviewable(file: ArtifactReceipt) {
  return (
    ["image/png", "image/jpeg", "image/webp"].includes(file.content_type) &&
    file.size <= 20 * 1024 * 1024
  );
}
export async function verifiedArtifact(
  token: Token,
  file: ArtifactReceipt,
  signal?: AbortSignal,
) {
  const response = await apiFetch(
    token,
    `/v1/app-runs/${file.run_id}/artifacts/${file.artifact_id}/content`,
    { signal },
  );
  const blob = await response.blob();
  if (blob.size !== file.size)
    throw new Error(
      "Downloaded file size does not match its published receipt.",
    );
  const digest = Array.from(
    new Uint8Array(
      await crypto.subtle.digest("SHA-256", await blob.arrayBuffer()),
    ),
  )
    .map((value) => value.toString(16).padStart(2, "0"))
    .join("");
  if (digest !== file.sha256)
    throw new Error(
      "Downloaded file hash does not match its published receipt.",
    );
  return new Blob([blob], { type: file.content_type });
}
