import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";
import { RobotAppearanceEditor } from "./RobotAppearanceEditor";
import { RobotArtwork } from "./RobotArtwork";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});
const robot = {
  id: "virt2",
  organization_id: "org",
  name: "virt2",
  appearance: "bot",
};
const token = async () => "token";

test("saves the Base101 image against the exact robot and retains it on failure", async () => {
  const user = userEvent.setup();
  const onSaved = vi.fn();
  const fetch = vi
    .fn()
    .mockResolvedValueOnce(
      Response.json({ error: { message: "Save failed" } }, { status: 500 }),
    )
    .mockResolvedValueOnce(Response.json({ ...robot, appearance: "base101" }));
  vi.stubGlobal("fetch", fetch);
  render(
    <RobotAppearanceEditor robot={robot} token={token} onSaved={onSaved} />,
  );
  await user.click(screen.getByRole("radio", { name: "Base101 image" }));
  await user.click(screen.getByRole("button", { name: "Save appearance" }));
  expect(await screen.findByRole("alert")).toBeTruthy();
  expect(onSaved).not.toHaveBeenCalled();
  expect(
    (screen.getByRole("radio", { name: "Base101 image" }) as HTMLInputElement)
      .checked,
  ).toBe(true);
  await user.click(screen.getByRole("button", { name: "Save appearance" }));
  expect(await screen.findByText("Robot appearance saved.")).toBeTruthy();
  expect(onSaved).toHaveBeenCalledOnce();
  const [url, request] = fetch.mock.calls[1];
  expect(url).toBe("/api/v1/robots/virt2/appearance");
  expect(request.method).toBe("PUT");
  expect(JSON.parse(request.body)).toEqual({ appearance: "base101" });
});

test("rejects unsupported uploads without replacing the selected appearance", async () => {
  const user = userEvent.setup({ applyAccept: false });
  render(
    <RobotAppearanceEditor robot={robot} token={token} onSaved={vi.fn()} />,
  );
  await user.upload(
    screen.getByLabelText("Upload an image"),
    new File(["<svg/>"], "robot.svg", { type: "image/svg+xml" }),
  );
  expect(
    await screen.findByText("Choose a PNG, JPEG or WebP image."),
  ).toBeTruthy();
  expect(
    (
      screen.getByRole("radio", {
        name: /^Robot$/,
      }) as HTMLInputElement
    ).checked,
  ).toBe(true);
});

test("renders Base101 artwork and a default icon for legacy robots", () => {
  const { container, rerender } = render(
    <RobotArtwork robot={{ appearance: "base101" }} />,
  );
  expect(container.querySelector("img")?.getAttribute("src")).toBe(
    "/robot-artwork/base101.png",
  );
  rerender(<RobotArtwork robot={{}} />);
  expect(container.querySelector("svg")).toBeTruthy();
  expect(container.querySelector("img")).toBeNull();
});

test("resizes an uploaded image and saves its preview value", async () => {
  const user = userEvent.setup();
  const close = vi.fn();
  const drawImage = vi.fn();
  const image = "data:image/webp;base64,UklGRgAAAABXRUJQ";
  vi.stubGlobal(
    "createImageBitmap",
    vi.fn(async () => ({ width: 2048, height: 1024, close })),
  );
  const context = vi
    .spyOn(HTMLCanvasElement.prototype, "getContext")
    .mockReturnValue({ drawImage } as unknown as CanvasRenderingContext2D);
  const encode = vi
    .spyOn(HTMLCanvasElement.prototype, "toDataURL")
    .mockReturnValue(image);
  const fetch = vi.fn<typeof globalThis.fetch>(async () =>
    Response.json({ ...robot, appearance: image }),
  );
  vi.stubGlobal("fetch", fetch);
  try {
    const { container } = render(
      <RobotAppearanceEditor robot={robot} token={token} onSaved={vi.fn()} />,
    );
    await user.upload(
      screen.getByLabelText("Upload an image"),
      new File(["image"], "robot.png", { type: "image/png" }),
    );
    expect(close).toHaveBeenCalledOnce();
    expect(drawImage.mock.calls[0].slice(1)).toEqual([0, 0, 512, 256]);
    expect(container.querySelector(`img[src="${image}"]`)).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Save appearance" }));
    await screen.findByText("Robot appearance saved.");
    expect(JSON.parse(String(fetch.mock.calls[0][1]?.body))).toEqual({
      appearance: image,
    });
  } finally {
    context.mockRestore();
    encode.mockRestore();
  }
});
