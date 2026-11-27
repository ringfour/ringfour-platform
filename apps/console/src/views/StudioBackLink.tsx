import { ArrowLeft } from "lucide-react";
import { createContext, useContext } from "react";
import { useLocation } from "@/lib/location";
import { chromeBackLink } from "@/lib/studio-chrome";

export const StudioWorkspace = createContext<string | undefined>(undefined);

export function StudioBackLink() {
  const workspace = useContext(StudioWorkspace);
  const url = useLocation();
  const back = workspace ? chromeBackLink(url, workspace) : undefined;
  return back ? (
    <a href={back.href} className="studio-page-back">
      <ArrowLeft size={18} aria-hidden="true" />
      <span>{back.label}</span>
    </a>
  ) : null;
}
