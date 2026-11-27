import { Blob as NodeBlob } from "node:buffer";
import { createHash, webcrypto } from "node:crypto";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";
import { AppFiles } from "@/features/files/AppFiles";

const token = async () => "token";
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});
function setup({
  tampered = false,
  svg = false,
  fixedRunId = undefined as string | undefined,
} = {}) {
  const bytes = new Uint8Array([1, 2, 3]);
  const file = {
    artifact_id: "photo",
    run_id: "run",
    robot_id: "robot",
    installation_id: "installation",
    path: "snapshot.png",
    content_type: svg ? "image/svg+xml" : "image/png",
    size: bytes.length,
    sha256: tampered
      ? "0".repeat(64)
      : createHash("sha256").update(bytes).digest("hex"),
    published_at: "2026-11-25T10:00:00Z",
  };
  const release = vi.fn();
  const NativeURL = URL;
  vi.stubGlobal(
    "URL",
    class extends NativeURL {
      static createObjectURL = vi.fn(() => "blob:test-result");
      static revokeObjectURL = release;
    },
  );
  vi.stubGlobal("crypto", webcrypto);
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL) =>
      String(input).endsWith("/content")
        ? Object.defineProperty(
            new Response(bytes, {
              headers: { "content-type": file.content_type },
            }),
            "blob",
            {
              value: async () =>
                new NodeBlob([bytes], { type: file.content_type }),
            },
          )
        : Response.json({ items: [file], next_cursor: null }),
    ),
  );
  render(
    <AppFiles
      fixedRunId={fixedRunId}
      appId="camera"
      organizationId="org"
      installationId="installation"
      robots={[{ id: "robot", name: "virt2" }]}
      runs={[]}
      token={token}
    />,
  );
  return release;
}
test("photo preview verifies published receipt and releases the temporary URL on dismissal", async () => {
  const release = setup();
  await userEvent.click(await screen.findByRole("button", { name: "Preview" }));
  expect(
    await screen.findByRole("img", { name: "Published result: snapshot.png" }),
  ).toHaveProperty("src", "blob:test-result");
  await userEvent.click(screen.getByRole("button", { name: "Close" }));
  expect(release).toHaveBeenCalledWith("blob:test-result");
});
test("a mismatched receipt refuses preview and SVG remains download-only", async () => {
  setup({ tampered: true });
  await userEvent.click(await screen.findByRole("button", { name: "Preview" }));
  expect(await screen.findByRole("alert")).toHaveProperty(
    "textContent",
    expect.stringContaining("hash does not match"),
  );
  expect(screen.queryByRole("img")).toBeNull();
  cleanup();
  setup({ svg: true });
  await screen.findAllByText("snapshot.png");
  expect(screen.queryByRole("button", { name: "Preview" })).toBeNull();
  expect(screen.getByRole("button", { name: "Download" })).toBeTruthy();
});

test("run results keep their exact run scope and cannot switch to other runs", async () => {
  setup({ fixedRunId: "exact-run" });
  await screen.findAllByText("snapshot.png");
  const requests = vi
    .mocked(fetch)
    .mock.calls.map(([url]) => new URL(String(url), "http://localhost"));
  expect(
    requests.some(
      (url) =>
        url.searchParams.get("run_id") === "exact-run" &&
        url.searchParams.get("installation_id") === "installation",
    ),
  ).toBe(true);
  expect(screen.queryByLabelText("Run")).toBeNull();
});

test("visible photo thumbnails verify receipts and release URLs on unmount", async () => {
  vi.stubGlobal(
    "IntersectionObserver",
    class {
      constructor(
        private callback: (entries: { isIntersecting: boolean }[]) => void,
      ) {}
      observe() {
        this.callback([{ isIntersecting: true }]);
      }
      disconnect() {}
    },
  );
  const release = setup();
  expect(
    await screen.findByRole("img", { name: "Published photo: snapshot.png" }),
  ).toHaveProperty("src", "blob:test-result");
  await userEvent.click(screen.getByRole("button", { name: "Refresh" }));
  await screen.findByRole("img", { name: "Published photo: snapshot.png" });
  expect(
    vi
      .mocked(fetch)
      .mock.calls.filter(([input]) => String(input).endsWith("/content")),
  ).toHaveLength(1);
  cleanup();
  expect(release).toHaveBeenCalledWith("blob:test-result");
});

test("a tampered thumbnail shows the verification failure instead of an image", async () => {
  vi.stubGlobal(
    "IntersectionObserver",
    class {
      constructor(
        private callback: (entries: { isIntersecting: boolean }[]) => void,
      ) {}
      observe() {
        this.callback([{ isIntersecting: true }]);
      }
      disconnect() {}
    },
  );
  setup({ tampered: true });
  await screen.findByText(/hash does not match/);
  expect(screen.queryByRole("img")).toBeNull();
});

test("applying the same filters reloads results instead of leaving the list empty", async () => {
  setup();
  await screen.findAllByText("snapshot.png");
  const reads = () =>
    vi
      .mocked(fetch)
      .mock.calls.filter(([url]) => String(url).includes("/artifacts?")).length;
  const before = reads();
  await userEvent.click(screen.getByRole("button", { name: "Apply filters" }));
  await screen.findAllByText("snapshot.png");
  expect(reads()).toBeGreaterThan(before);
});
