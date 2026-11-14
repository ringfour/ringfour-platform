import { useEffect, useState } from "react";

import { PageHeading } from "@/components/PageHeading";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { StatusBadge } from "@/components/ui/status-badge";

type ServiceHealth = {
  name: string;
  status: "ok" | "fail" | "unreachable";
  version?: string;
  checks?: Record<string, string>;
};

const SERVICES: { name: string; base: string }[] = [
  { name: "api", base: "/api" },
  { name: "daemon", base: "/daemon" },
];

async function fetchServiceHealth(
  name: string,
  base: string,
): Promise<ServiceHealth> {
  try {
    const response = await fetch(`${base}/health/ready`);
    const body = await response.json();
    return {
      name,
      status: response.ok && body.status === "ok" ? "ok" : "fail",
      version: body.version,
      checks: body.checks,
    };
  } catch {
    return { name, status: "unreachable" };
  }
}

export function HealthPage() {
  const [services, setServices] = useState<ServiceHealth[] | null>(null);

  useEffect(() => {
    Promise.all(SERVICES.map((s) => fetchServiceHealth(s.name, s.base))).then(
      setServices,
    );
  }, []);

  return (
    <main className="workspace-page">
      <PageHeading
        title="System health"
        description="Check the services connected to this Console."
      />
      <Card>
        <CardHeader>
          <CardTitle>Service health</CardTitle>
        </CardHeader>
        <CardContent>
          {services === null ? (
            <p className="text-muted-foreground">Checking…</p>
          ) : (
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-left text-muted-foreground">
                  <th className="py-2 font-medium">Service</th>
                  <th className="py-2 font-medium">Status</th>
                  <th className="py-2 font-medium">Version</th>
                  <th className="py-2 font-medium">Checks</th>
                </tr>
              </thead>
              <tbody>
                {services.map((service) => (
                  <tr key={service.name} className="border-b last:border-0">
                    <td className="py-2 font-medium">{service.name}</td>
                    <td className="py-2">
                      <StatusBadge
                        data-testid={`${service.name}-status`}
                        variant={
                          service.status === "ok"
                            ? "success"
                            : service.status === "fail"
                              ? "danger"
                              : "neutral"
                        }
                        label={service.status}
                      />
                    </td>
                    <td className="py-2">{service.version ?? "?"}</td>
                    <td className="py-2 text-muted-foreground">
                      {service.checks
                        ? Object.entries(service.checks)
                            .map(([k, v]) => `${k}=${v}`)
                            .join(", ")
                        : "—"}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </CardContent>
      </Card>
    </main>
  );
}
