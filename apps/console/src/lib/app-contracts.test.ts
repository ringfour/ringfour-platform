import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { readAppFailure, readAppOperation } from "./app-contracts";

const fixtures = JSON.parse(
  readFileSync(
    resolve(
      process.cwd(),
      "../../packages/contracts/fixtures/apps/v1/summaries.json",
    ),
    "utf8",
  ),
);
describe("AP-01 App response summaries", () => {
  for (const fixture of fixtures) {
    it(fixture.name, () => {
      const read =
        fixture.kind === "operation" ? readAppOperation : readAppFailure;
      expect(read(fixture.input)).toEqual(fixture.expected);
    });
  }
  it("rejects unknown states and acknowledgement presented as success", () => {
    const ack = fixtures.find(
      (f: { name: string }) => f.name === "install_ack",
    ).input;
    expect(() => readAppOperation({ ...ack, state: "invented" })).toThrow();
    expect(() => readAppOperation({ ...ack, id: `${ack.id}\n` })).toThrow();
    expect(() => readAppOperation({ ...ack, state: "succeeded" })).toThrow();
    expect(() =>
      readAppOperation({ ...ack, phase: "observed_running" }),
    ).toThrow();
  });
});
