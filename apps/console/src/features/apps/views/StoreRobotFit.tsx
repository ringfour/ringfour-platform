import { useEffect, useState } from "react";
import { compatibilityMessage } from "@/lib/app-compatibility";
import {
  type Compatibility,
  type Robot,
  readJson,
  type Token,
} from "@/lib/apps";
import { capabilityInstance, describeCapability } from "@/lib/capabilities";
import type { RobotAppStatus } from "@/lib/robot-status";

export function StoreRobotFit({
  robot,
  compatibility,
  token,
  camera,
  reportedStatus,
}: {
  robot?: Robot;
  compatibility?: Compatibility | string;
  token: Token;
  camera: boolean;
  reportedStatus?: RobotAppStatus;
}) {
  const [status, setStatus] = useState<RobotAppStatus>();
  const robotId = robot?.id;
  const compatible =
    typeof compatibility === "object" && compatibility.status === "compatible";
  useEffect(() => {
    if (!robotId || !camera || !compatible || reportedStatus) return;
    let live = true;
    void readJson<RobotAppStatus>(token, `/v1/robots/${robotId}/app-status`)
      .then((value) => {
        if (live) setStatus(value);
      })
      .catch(() => {});
    return () => {
      live = false;
    };
  }, [robotId, camera, compatible, token, reportedStatus]);

  if (!robot)
    return <p>Connect a robot to check whether it can run this App.</p>;
  if (typeof compatibility === "string")
    return (
      <p role="status">
        Couldn’t check {robot.name}: {compatibility}
      </p>
    );
  if (!compatibility) return <p role="status">Checking {robot.name}…</p>;
  const observation = (reportedStatus ?? status)?.inventory?.current;
  const cameraNames =
    observation?.observed_at === compatibility.inventory_observed_at
      ? observation?.instances?.cameras
      : undefined;
  return compatible ? (
    <p className="text-success" role="status">
      Works with {robot.name}
      {cameraNames?.length === 1 &&
        ` · uses its ${capabilityInstance("cameras", cameraNames[0]).toLowerCase()}`}
    </p>
  ) : (
    <p role="status">
      {robot.name}: {compatibilityMessage(compatibility.status)}
      {compatibility.missing_native?.length > 0 &&
        ` Missing components: ${compatibility.missing_native.join(", ")}.`}
      {compatibility.missing_required.length > 0 &&
        ` Missing: ${compatibility.missing_required.map((item) => describeCapability(item.capability).name).join(", ")}.`}
    </p>
  );
}
