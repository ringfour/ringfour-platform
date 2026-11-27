import type { AppValues, Installation, Revision } from "@/lib/apps";
export type AppsScope = {
  organizationId: string;
  workspaceRole?: string;
  focusAppId?: string;
  focusSection?: string;
};
export type StartupEdit = {
  installation: Installation;
  revision: Revision;
  values: AppValues;
};
