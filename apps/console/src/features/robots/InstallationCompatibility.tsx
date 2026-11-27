import { useEffect, useState } from "react";
import { StoreRobotFit } from "@/features/apps/views/StoreRobotFit";
import {
  type Compatibility,
  type Robot,
  readJson,
  type Token,
  withQuery,
} from "@/lib/apps";

export function InstallationCompatibility({
  robot,
  revisionId,
  token,
}: {
  robot: Robot;
  revisionId: string;
  token: Token;
}) {
  const [open, setOpen] = useState(false);
  const [result, setResult] = useState<{
    revisionId: string;
    value: Compatibility | string;
  }>();
  const robotId = robot.id;
  useEffect(() => {
    if (!open) return;
    let live = true;
    setResult(undefined);
    void readJson<Compatibility>(
      token,
      withQuery(`/v1/robots/${robotId}/app-compatibility`, {
        revision_id: revisionId,
      }),
    )
      .then((value) => {
        if (live) setResult({ revisionId, value });
      })
      .catch(() => {
        if (live)
          setResult({ revisionId, value: "Compatibility is unavailable." });
      });
    return () => {
      live = false;
    };
  }, [open, revisionId, robotId, token]);

  return (
    <details
      className="mt-2 text-xs text-muted-foreground"
      onToggle={(event) => setOpen(event.currentTarget.open)}
    >
      <summary>Compatibility</summary>
      {open && (
        <div className="mt-2">
          <StoreRobotFit
            robot={robot}
            compatibility={
              result?.revisionId === revisionId ? result.value : undefined
            }
            token={token}
            camera={false}
          />
        </div>
      )}
    </details>
  );
}
