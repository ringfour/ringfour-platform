import { useEffect, useRef } from "react";

/** Native modal focus containment, Escape dismissal and focus return. */
export function StudioDialog({
  title,
  children,
  onClose,
  focusHeading = false,
}: {
  title: string;
  focusHeading?: boolean;
  children: React.ReactNode;
  onClose: () => void;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    const node = dialog.current;
    if (node && !node.open) {
      if (typeof node.showModal === "function") node.showModal();
      else node.setAttribute("open", "");
    }
    if (focusHeading) node?.querySelector("h2")?.focus();
    return () => {
      if (node && typeof node.close === "function") node.close();
      previous?.focus();
    };
  }, [focusHeading]);
  return (
    <dialog
      ref={dialog}
      className="studio-dialog"
      aria-label={title}
      onCancel={(event) => {
        event.preventDefault();
        onClose();
      }}
    >
      <header>
        <h2 tabIndex={focusHeading ? -1 : undefined}>{title}</h2>
        <button className="button-secondary" type="button" onClick={onClose}>
          Close
        </button>
      </header>
      {children}
    </dialog>
  );
}
