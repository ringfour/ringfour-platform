import { Bot, CarFront, Cog, Plane } from "lucide-react";
import type { Robot } from "@/lib/apps";
import "./robots.css";

export const robotLooks = [
  ["bot", "Robot"],
  ["rover", "Rover"],
  ["arm", "Arm"],
  ["drone", "Drone"],
  ["base101", "Base101 image"],
] as const;

export function RobotArtwork({
  robot,
  className = "h-12 w-12",
}: {
  robot: Pick<Robot, "appearance">;
  className?: string;
}) {
  const appearance = robot.appearance ?? "bot";
  const image =
    appearance === "base101"
      ? "/robot-artwork/base101.png"
      : /^data:image\/(png|jpeg|webp);base64,/.test(appearance)
        ? appearance
        : null;
  const icons: Record<string, typeof Bot> = {
    rover: CarFront,
    arm: Cog,
    drone: Plane,
  };
  const Icon = icons[appearance] ?? Bot;
  return (
    <span className={`robot-artwork ${className}`} aria-hidden="true">
      {image ? <img src={image} alt="" /> : <Icon strokeWidth={1.5} />}
    </span>
  );
}
