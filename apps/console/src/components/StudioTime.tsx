import { useSyncExternalStore } from "react";

const listeners = new Set<() => void>();
let timer: number | undefined;
let currentMinute = Date.now();
function subscribe(listener: () => void) {
  listeners.add(listener);
  if (timer === undefined) {
    currentMinute = Date.now();
    timer = window.setInterval(() => {
      currentMinute = Date.now();
      for (const notify of listeners) notify();
    }, 60000);
  }
  return () => {
    listeners.delete(listener);
    if (!listeners.size) {
      window.clearInterval(timer);
      timer = undefined;
    }
  };
}
const snapshot = () => currentMinute;
export function StudioTime({ value }: { value: string }) {
  const now = useSyncExternalStore(subscribe, snapshot);
  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) return <>Time unknown</>;
  const minutes = Math.round((date.getTime() - now) / 60000);
  const unit =
    Math.abs(minutes) < 60
      ? "minute"
      : Math.abs(minutes) < 1440
        ? "hour"
        : "day";
  const age = new Intl.RelativeTimeFormat(undefined, {
    numeric: "auto",
  }).format(
    Math.round(minutes / (unit === "hour" ? 60 : unit === "day" ? 1440 : 1)),
    unit,
  );
  return (
    <time
      dateTime={value}
      title={date.toLocaleString(undefined, { timeZoneName: "short" })}
    >
      {date.toLocaleString(undefined, {
        year: "numeric",
        month: "short",
        day: "numeric",
        hour: "numeric",
        minute: "2-digit",
      })}{" "}
      · {age}
    </time>
  );
}
