import type { AppLogbookEvent, AppRun, Token } from "./apps";
import { readJson, withQuery } from "./apps";

export const activeRun = (run: AppRun) =>
  ["accepted", "running"].includes(run.state);
export function eventTitle(event: Pick<AppLogbookEvent, "type" | "payload">) {
  const titles: Record<string, string> = {
    "app.settings.application_requested": "Live settings requested",
    "app.settings.ended_before_confirmation":
      "Run ended before settings were confirmed",
    "app.settings.applied": "Settings applied",
    "app.settings.rejected": "Settings rejected",
    "app.settings.selected": "Settings selected for next start",
    "human.interaction.requested": "Decision requested",
    "human.interaction.answered": "Decision answered",
    "mission.failed": "Run failed",
    "app.run.state_changed": "Run state changed",
  };
  return (
    titles[event.type] ??
    (typeof event.payload.message === "string" && event.payload.message.trim()
      ? event.payload.message
      : undefined) ??
    event.type
      .replace(/^app\./, "")
      .replaceAll(/[._]/g, " ")
      .replace(/^./, (letter) => letter.toUpperCase())
  );
}
export function eventTone(event: AppLogbookEvent) {
  const state = String(event.payload.state ?? event.payload.event_type ?? "");
  if (
    /fail|reject|interrupt|refus|guard_tripped/.test(`${event.type} ${state}`)
  )
    return "danger";
  if (/succeed|applied|completed|stop_verified/.test(`${event.type} ${state}`))
    return "success";
  if (/requested|running|accepted/.test(`${event.type} ${state}`))
    return "active";
  return typeof event.payload.message === "string" ? "note" : "neutral";
}
export async function readRunOutput(
  token: Token,
  runId: string,
  stream: "stdout" | "stderr",
) {
  const decoder = new TextDecoder();
  let text = "";
  let offset = 0;
  let truncated = false;
  for (let pageNumber = 0; pageNumber < 5; pageNumber++) {
    const page = await readJson<{
      next_offset: number;
      data_base64: string;
      sealed: boolean;
      truncated: boolean;
    }>(
      token,
      withQuery(`/v1/app-runs/${runId}/output`, {
        stream,
        offset: String(offset),
      }),
    );
    const bytes = Uint8Array.from(atob(page.data_base64), (character) =>
      character.charCodeAt(0),
    );
    text += decoder.decode(bytes, { stream: true });
    truncated ||= page.truncated;
    if (page.sealed || page.next_offset <= offset || !bytes.length) break;
    offset = page.next_offset;
    if (pageNumber === 4) truncated = true;
  }
  return { text: text + decoder.decode(), truncated };
}
