import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";
import { type Artifact, ArtifactGallery } from "./ArtifactGallery";

const originalCreateObjectURL = Object.getOwnPropertyDescriptor(
  URL,
  "createObjectURL",
);
const originalRevokeObjectURL = Object.getOwnPropertyDescriptor(
  URL,
  "revokeObjectURL",
);

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  if (originalCreateObjectURL) {
    Object.defineProperty(URL, "createObjectURL", originalCreateObjectURL);
  } else {
    Reflect.deleteProperty(URL, "createObjectURL");
  }
  if (originalRevokeObjectURL) {
    Object.defineProperty(URL, "revokeObjectURL", originalRevokeObjectURL);
  } else {
    Reflect.deleteProperty(URL, "revokeObjectURL");
  }
});

test("loads authenticated image previews and opens the full-size view", async () => {
  const getToken = vi.fn(async () => "access-token");
  const fetchMock = vi.fn(
    async () => new Response(new Blob(["photo"]), { status: 200 }),
  );
  const createObjectURL = vi.fn(() => "blob:mission-photo");
  vi.stubGlobal("fetch", fetchMock);
  Object.defineProperties(URL, {
    createObjectURL: { configurable: true, value: createObjectURL },
    revokeObjectURL: { configurable: true, value: vi.fn() },
  });
  const artifact: Artifact = {
    artifact_id: "artifact-1",
    path: "photos/turn.jpg",
    size: 512,
    sha256: "hash",
    content_type: "image/jpeg",
    state: "available",
  };

  render(
    <ArtifactGallery
      artifacts={[artifact]}
      executionId="execution-1"
      getToken={getToken}
      sectionIndex="02"
    />,
  );

  const preview = await screen.findByRole("img", {
    name: "Mission artifact photos/turn.jpg",
  });
  expect(preview.getAttribute("src")).toBe("blob:mission-photo");
  expect(fetchMock).toHaveBeenCalledWith(
    "/api/v1/executions/execution-1/artifacts/content?path=photos%2Fturn.jpg",
    expect.objectContaining({
      headers: { Authorization: "Bearer access-token" },
    }),
  );

  await userEvent.click(
    screen.getByRole("button", { name: "Open preview for turn.jpg" }),
  );
  expect(
    await screen.findByRole("dialog", { name: "Preview turn.jpg" }),
  ).toBeTruthy();
  await userEvent.click(
    screen.getByRole("button", { name: "Close image preview" }),
  );
  await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
});
