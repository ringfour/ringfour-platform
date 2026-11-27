import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, expect, test, vi } from "vitest";
import type { AppSchema, AppValues } from "@/lib/apps";
import { SchemaFields } from "./SchemaFields";

afterEach(cleanup);
const schema: AppSchema = {
  properties: {
    gain: { type: "number", minimum: 0, maximum: 10 },
    count: { type: "integer", minimum: 1, maximum: 20 },
    enabled: { type: "boolean" },
    gate: { type: "boolean", enum: [true, false] },
    mode: { type: "string", enum: ["auto", "manual"] },
  },
  required: ["count", "gate"],
};
function Editor({
  onChange = () => {},
  disabled = false,
}: {
  onChange?: (values: AppValues) => void;
  disabled?: boolean;
}) {
  const [values, setValues] = useState<AppValues>({
    gain: 0.5,
    count: 2,
    enabled: true,
    gate: true,
    mode: "auto",
  });
  return (
    <SchemaFields
      schema={schema}
      prefix="test"
      values={values}
      disabled={disabled}
      onChange={(next) => {
        setValues(next);
        onChange(next);
      }}
      feedback={(key) => `Help for ${key}`}
    />
  );
}
test("fractional and integer controls retain their declared numeric constraints", () => {
  const change = vi.fn();
  render(<Editor onChange={change} />);
  const gain = screen.getByLabelText("gain") as HTMLInputElement;
  const count = screen.getByLabelText("count") as HTMLInputElement;
  fireEvent.change(gain, { target: { value: "0.125" } });
  expect(change.mock.lastCall?.[0].gain).toBe(0.125);
  expect(gain.step).toBe("any");
  expect(gain.validity.valid).toBe(true);
  fireEvent.change(count, { target: { value: "1.5" } });
  expect(count.step).toBe("1");
  expect(count.validity.stepMismatch).toBe(true);
  fireEvent.change(count, { target: { value: "21" } });
  expect(count.validity.rangeOverflow).toBe(true);
});
test("cleared optional numbers are omitted while false enum values remain booleans", async () => {
  const change = vi.fn();
  render(<Editor onChange={change} />);
  await userEvent.selectOptions(screen.getByLabelText("gate"), "false");
  expect(change.mock.lastCall?.[0].gate).toBe(false);
  await userEvent.clear(screen.getByLabelText("gain"));
  expect(change.mock.lastCall?.[0]).not.toHaveProperty("gain");
  expect(change.mock.lastCall?.[0].gate).toBe(false);
  await userEvent.selectOptions(screen.getByLabelText("mode"), "manual");
  expect(change.mock.lastCall?.[0].mode).toBe("manual");
});
test("pending/readonly fields disable every control type", async () => {
  const change = vi.fn();
  render(<Editor disabled onChange={change} />);
  for (const key of Object.keys(schema.properties))
    expect(screen.getByLabelText(key)).toHaveProperty("disabled", true);
  await userEvent.click(screen.getByLabelText("enabled"));
  expect(change).not.toHaveBeenCalled();
});
test("every field type links feedback to the correctly scoped control", () => {
  render(<Editor />);
  for (const key of Object.keys(schema.properties)) {
    const field = screen.getByLabelText(key);
    const help = field.getAttribute("aria-describedby");
    expect(help).toBe(`test-${key}-feedback`);
    expect(document.getElementById(help ?? "")?.textContent).toBe(
      `Help for ${key}`,
    );
  }
});
