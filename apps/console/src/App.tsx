import { HealthPage } from "@/components/HealthPage";

export default function App() {
  if (!import.meta.env.DEV) {
    return (
      <main className="mx-auto max-w-2xl p-6">
        <p>Ringfour Console is a development-only tool.</p>
      </main>
    );
  }
  return <HealthPage />;
}
