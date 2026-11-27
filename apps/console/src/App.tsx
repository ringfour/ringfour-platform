import { AuthGate } from "@/app/AuthGate";
import { StudioWorkspace } from "@/app/StudioWorkspace";
export default function App() {
  return (
    <AuthGate>
      <StudioWorkspace />
    </AuthGate>
  );
}
