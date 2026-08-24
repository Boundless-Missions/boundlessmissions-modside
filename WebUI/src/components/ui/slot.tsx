import * as React from "react";

/**
 * Minimal `asChild` Slot implementation.
 * Merges the props/ref of the parent onto its single child element,
 * avoiding a runtime dependency on @radix-ui/react-slot.
 */
export const Slot = React.forwardRef<HTMLElement, React.HTMLAttributes<HTMLElement>>(
  ({ children, ...slotProps }, ref) => {
    if (!React.isValidElement(children)) {
      return null;
    }

    const child = children as React.ReactElement<Record<string, unknown>>;

    return React.cloneElement(child, {
      ...slotProps,
      ...child.props,
      className: [
        (slotProps as { className?: string }).className,
        (child.props as { className?: string }).className,
      ]
        .filter(Boolean)
        .join(" "),
      ref,
    });
  }
);
Slot.displayName = "Slot";
