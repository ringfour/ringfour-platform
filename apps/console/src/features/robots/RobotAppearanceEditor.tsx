import { useState } from "react";
import { apiFetch } from "@/lib/api";
import type { Robot, Token } from "@/lib/apps";
import { RobotAppearancePicker } from "./RobotAppearancePicker";

export function RobotAppearanceEditor({
  robot,
  token,
  onSaved,
}: {
  robot: Robot;
  token: Token;
  onSaved: () => void;
}) {
  const [value, setValue] = useState(robot.appearance ?? "bot");
  const [pending, setPending] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [saved, setSaved] = useState(false);
  return (
    <form
      onSubmit={async (event) => {
        event.preventDefault();
        if (saving || pending) return;
        setSaving(true);
        setError("");
        setSaved(false);
        try {
          await apiFetch(token, `/v1/robots/${robot.id}/appearance`, {
            method: "PUT",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ appearance: value }),
          });
          setSaved(true);
          onSaved();
        } catch (cause) {
          setError(
            cause instanceof Error
              ? cause.message
              : "Unable to save robot appearance.",
          );
        } finally {
          setSaving(false);
        }
      }}
      className="studio-form"
    >
      <fieldset disabled={saving}>
        <RobotAppearancePicker
          value={value}
          onChange={(next) => {
            setValue(next);
            setSaved(false);
          }}
          onPendingChange={setPending}
        />
      </fieldset>
      <div>
        <button
          type="submit"
          className="button-primary"
          disabled={saving || pending}
        >
          {saving ? "Saving…" : "Save appearance"}
        </button>
      </div>
      {saved && <p role="status">Robot appearance saved.</p>}
      {error && (
        <p role="alert" className="text-destructive">
          {error}
        </p>
      )}
    </form>
  );
}
