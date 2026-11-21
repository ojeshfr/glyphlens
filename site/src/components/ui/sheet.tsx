import * as React from "react";
import * as DialogPrimitive from "@radix-ui/react-dialog";
import { X } from "lucide-react";

import { cn } from "@/lib/utils";

/*
 * shadcn/ui Sheet (on the Radix Dialog primitive), adapted for a strict Content-Security-Policy.
 * Radix's DialogOverlay wraps itself in react-remove-scroll, which injects a <style> element at run time; `style-src 'self'`
 * blocks that. So this Sheet draws its backdrop as a plain element inside the portal and locks page scrolling with a class
 * on <html> (see index.css). Focus trapping, Escape, outside-click dismissal and aria-hidden on the rest of the page all
 * come from DialogContent and are unchanged.
 */

function Sheet(props: React.ComponentProps<typeof DialogPrimitive.Root>) {
  return <DialogPrimitive.Root data-slot="sheet" {...props} />;
}

function SheetTrigger(props: React.ComponentProps<typeof DialogPrimitive.Trigger>) {
  return <DialogPrimitive.Trigger data-slot="sheet-trigger" {...props} />;
}

function SheetClose(props: React.ComponentProps<typeof DialogPrimitive.Close>) {
  return <DialogPrimitive.Close data-slot="sheet-close" {...props} />;
}

/** Rendered inside DialogContent, so it exists (and locks page scrolling) only while the sheet is open. */
function ScrollLock() {
  React.useEffect(() => {
    const root = document.documentElement;
    root.classList.add("scroll-locked");
    return () => root.classList.remove("scroll-locked");
  }, []);
  return null;
}

function SheetContent({ className, children, ...props }: React.ComponentProps<typeof DialogPrimitive.Content>) {
  return (
    <DialogPrimitive.Portal>
      <div data-slot="sheet-backdrop" aria-hidden="true" className="fixed inset-0 z-40 animate-fade-in bg-black/40" />
      <DialogPrimitive.Content
        data-slot="sheet-content"
        className={cn(
          "fixed inset-x-0 top-0 z-50 flex max-h-dvh flex-col gap-2 overflow-y-auto rounded-b-2xl bg-nav-solid px-4 pt-3 pb-6 text-foreground shadow-panel outline-hidden animate-sheet-in",
          className,
        )}
        {...props}
      >
        <ScrollLock />
        {children}
        <DialogPrimitive.Close
          className="absolute top-3 right-3 inline-flex size-11 items-center justify-center rounded-full text-foreground transition-colors outline-hidden hover:bg-secondary focus-visible:outline-2 focus-visible:outline-solid focus-visible:outline-offset-2 focus-visible:outline-ring"
          aria-label="Close menu"
        >
          <X aria-hidden="true" className="size-5" />
        </DialogPrimitive.Close>
      </DialogPrimitive.Content>
    </DialogPrimitive.Portal>
  );
}

function SheetTitle({ className, ...props }: React.ComponentProps<typeof DialogPrimitive.Title>) {
  return <DialogPrimitive.Title data-slot="sheet-title" className={cn("text-base font-semibold", className)} {...props} />;
}

function SheetDescription({ className, ...props }: React.ComponentProps<typeof DialogPrimitive.Description>) {
  return <DialogPrimitive.Description data-slot="sheet-description" className={cn("text-sm text-muted-foreground", className)} {...props} />;
}

export { Sheet, SheetTrigger, SheetClose, SheetContent, SheetTitle, SheetDescription };
