import { useEffect, useState } from "react";
import {
  type AppOperation,
  appStateLabel,
  readJson,
  type Token,
} from "@/lib/apps";

export function AppOperationStatus({
  operationId,
  token,
}: {
  operationId: string;
  token: Token;
}) {
  const [operation, setOperation] = useState<AppOperation | null>(null);
  const [error, setError] = useState("");
  useEffect(() => {
    let active = true;
    let timer: number | undefined;
    const load = async () => {
      try {
        const value = await readJson<AppOperation>(
          token,
          `/v1/app-operations/${operationId}`,
        );
        if (!active) return;
        setOperation(value);
        setError("");
        if (["queued", "accepted", "running"].includes(value.state))
          timer = window.setTimeout(load, 2500);
      } catch (cause) {
        if (active)
          setError(
            cause instanceof Error
              ? cause.message
              : "Operation status unavailable.",
          );
      }
    };
    void load();
    return () => {
      active = false;
      window.clearTimeout(timer);
    };
  }, [operationId, token]);
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
