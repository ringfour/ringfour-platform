import {
  Blocks,
  Camera,
  Compass,
  Map as MapIcon,
  Radar,
  Radio,
  Route,
} from "lucide-react";

const descriptions = {
  cameras: {
    name: "Cameras",
    icon: Camera,
    unit: "camera",
    detail: "Captures still images",
  },
  mobility: {
    name: "Mobility",
    icon: Route,
    unit: "drive",
    detail: "Moves the robot base",
  },
  motion: {
    name: "Mobility",
    icon: Route,
    unit: "drive",
    detail: "Moves the robot base",
  },
  lidar: {
    name: "LiDAR",
    icon: Radar,
    unit: "LiDAR",
    detail: "Measures distance around the robot",
  },
  imu: {
    name: "IMU",
    icon: Compass,
    unit: "IMU",
    detail: "Measures acceleration and rotation",
  },
  mapping: {
    name: "Mapping",
    icon: MapIcon,
    unit: "map",
    detail: "Builds a map of the surroundings",
  },
  teleop: {
    name: "Teleoperation",
    icon: Radio,
    unit: "interface",
    detail: "Supports remote driving",
  },
  teleoperation: {
    name: "Teleoperation",
    icon: Radio,
    unit: "interface",
    detail: "Supports remote driving",
  },
};
export function describeCapability(capability: string) {
  return (
    descriptions[capability as keyof typeof descriptions] ?? {
      name: capability
        .replaceAll("_", " ")
        .replace(/^./, (letter) => letter.toUpperCase()),
      icon: Blocks,
      unit: "instance",
      detail: "",
    }
  );
}
export function capabilityInstance(capability: string, name: string) {
  return `${name.replaceAll("_", " ").replace(/^./, (letter) => letter.toUpperCase())} ${describeCapability(capability).unit}`;
}
