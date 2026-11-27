import { useEffect } from "react";

/** One guard for native unloads, Studio links, workspace changes and history. */
export function useUnsavedChanges(dirty: boolean, onDiscard: () => void) {
  useEffect(() => {
    if (!dirty) return;
    const unload = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = "";
    };
    const navigate = (event: Event) => {
      if (!window.confirm("Discard your unsaved settings changes?"))
        event.preventDefault();
      else onDiscard();
    };
    window.addEventListener("beforeunload", unload);
    window.addEventListener("ringfour:navigation", navigate);
    return () => {
      window.removeEventListener("beforeunload", unload);
      window.removeEventListener("ringfour:navigation", navigate);
    };
  }, [dirty, onDiscard]);
}
