import { useEffect, useState } from "react";
import { useApiToken } from "@/hooks/useApiToken";
import { apiFetchAll } from "@/lib/api";
import {
  parentPath,
  replaceQuery,
  type StudioRoute,
  type StudioView,
  studioPath,
  useLocation,
  visit,
} from "@/lib/location";
export function useWorkspace(route: StudioRoute) {
  const token = useApiToken();
  const location = useLocation();
  const [workspace, setOrganizationId] = useState("");
  const organizationId = location.searchParams.get("workspace") ?? workspace;
  const [workspaces, setWorkspaces] = useState<
    { id: string; name: string; role?: string }[]
  >([]);
  const activeOrganizationId = organizationId || workspaces[0]?.id || "";
  const activeRole = workspaces.find(
    (item) => item.id === activeOrganizationId,
  )?.role;
  useEffect(() => {
    let current = true;
    void apiFetchAll<{ id: string; name: string; role?: string }>(
      token,
      "/v1/organizations",
    )
      .then((items) => {
        if (current) setWorkspaces(items);
      })
      .catch(() => {});
    return () => {
      current = false;
    };
  }, [token]);
  const navigate = (next: StudioView, id = "", org = activeOrganizationId) => {
    const destination =
      !id && route.view === next && route.id
        ? parentPath(next, org)
        : studioPath(next, org, id);
    visit(destination);
  };

  const changeWorkspace = (target: string) => {
    const parent = route.view === "unavailable" ? "overview" : route.view;
    if (visit(studioPath(parent, target))) setOrganizationId(target);
  };
  const organizationLeft = (
    id: string,
    remaining: { id: string; name: string; role?: string }[],
  ) => {
    setWorkspaces(remaining);
    const currentWorkspace =
      new URL(window.location.href).searchParams.get("workspace") ??
      activeOrganizationId;
    if (currentWorkspace === id) {
      const next = remaining[0]?.id ?? "";
      setOrganizationId(next);
      replaceQuery({ workspace: next || undefined });
    }
  };
  const organizationJoined = (organization: {
    id: string;
    name: string;
    role?: string;
  }) => {
    setWorkspaces((current) => [
      ...current.filter((item) => item.id !== organization.id),
      organization,
    ]);
    setOrganizationId(organization.id);
    replaceQuery({ workspace: organization.id });
  };
  return {
    organizationJoined,
    organizationLeft,
    workspaces,
    workspace: activeOrganizationId,
    role: activeRole,
    navigate,
    changeWorkspace,
  };
}
