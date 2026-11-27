import { useEffect, useRef, useState } from "react";
import {
  type AppOperation,
  appStateLabel,
  readJson,
  type Token,
} from "@/lib/apps";

export function AppOperationStatus({
  operationId,
  token,
  onTerminal,
  installationId,
  organizationId,
}: {
  operationId: string;
  token: Token;
  onTerminal?: () => void;
  installationId?: string;
  organizationId?: string;
}) {
  const [operation, setOperation] = useState<AppOperation | null>(null);
  const [error, setError] = useState("");
  const notified = useRef("");
  useEffect(() => {
    let active = true;
    setOperation(null);
    setError("");
    let timer: number | undefined;
    const load = async () => {
      try {
        const value = await readJson<AppOperation>(
          token,
          `/v1/app-operations/${operationId}`,
        );
        if (!active) return;
        if (
          (installationId && value.installation_id !== installationId) ||
          (organizationId && value.organization_id !== organizationId)
        )
          throw new Error(
            "This operation is unavailable in the selected installation or workspace.",
          );
        setOperation(value);
        setError("");
        if (["queued", "accepted", "running"].includes(value.state))
          timer = window.setTimeout(load, 500);
        else if (
          ["succeeded", "failed", "cancelled"].includes(value.state) &&
          notified.current !== operationId
        ) {
          notified.current = operationId;
          onTerminal?.();
        }
      } catch (cause) {
        if (active) {
          timer = window.setTimeout(load, 1500);
          setError(
            cause instanceof Error
              ? cause.message
              : "Operation status unavailable.",
          );
        }
      }
    };
    void load();
    return () => {
      active = false;
      window.clearTimeout(timer);
    };
  }, [onTerminal, operationId, installationId, organizationId, token]);
  return (
    <p
      className="app-operation-status"
      role={error || operation?.state === "failed" ? "alert" : "status"}
    >
      {error ||
        (operation
          ? `${appStateLabel(operation.state, operation.phase)}${operation.failure ? ` · ${operation.failure.code}: ${operation.failure.message}` : ""}`
          : "Loading operation status…")}
    </p>
  );
}
