import { cn } from "@/lib/utils";

type BadgeVariant = "success" | "danger" | "neutral";

const variantColor: Record<BadgeVariant, string> = {
  success: "text-ready-text",
  danger: "text-danger",
  neutral: "text-muted-foreground",
};

// Status labels accompany color per the Ringfour interface system.
export function StatusBadge({
  variant,
  label,
  className,
  ...props
}: React.ComponentProps<"span"> & { variant: BadgeVariant; label: string }) {
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1.5 font-mono text-xs whitespace-nowrap",
        variantColor[variant],
        className,
      )}
      {...props}
    >
      <span className="size-1.5 rounded-full bg-current" aria-hidden="true" />
      {label}
    </span>
  );
}
