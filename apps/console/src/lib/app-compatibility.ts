import type { Compatibility } from "./apps";

export function compatibilityMessage(status: Compatibility["status"]) {
  switch (status) {
    case "compatible":
      return "This robot meets the release requirements.";
    case "daemon_upgrade_required":
      return "The robot needs an App runtime update before it can run this release.";
    case "inventory_unavailable":
      return "Robot capability inventory is not available yet.";
    case "missing_required":
      return "This robot is missing a required capability.";
    case "no_matching_image":
      return "This release has no image for the robot platform.";
    case "native_requirement_unavailable":
      return "This robot is missing a required native component.";
  }
}
