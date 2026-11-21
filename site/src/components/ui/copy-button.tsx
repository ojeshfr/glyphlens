import * as React from "react";
import { Check, Copy } from "lucide-react";

import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";

type CopyState = "idle" | "copied" | "failed";

/** Copies `value` to the clipboard. The result is announced to screen readers through a polite live region. */
function CopyButton({ value, label, className }: { value: string; label: string; className?: string }) {
  const [state, setState] = React.useState<CopyState>("idle");
  const timer = React.useRef<number | undefined>(undefined);

  React.useEffect(() => () => window.clearTimeout(timer.current), []);

  async function copy() {
    let ok: boolean;
    try {
      await navigator.clipboard.writeText(value);
      ok = true;
    } catch {
      ok = false;
    }
    setState(ok ? "copied" : "failed");
    window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => setState("idle"), 2000);
  }

  const tip = state === "copied" ? "Copied" : state === "failed" ? "Copy failed: select the text instead" : label;
  return (
    <>
      <Tooltip>
        <TooltipTrigger asChild>
          <Button type="button" variant="secondary" size="icon" className={cn("size-11", className)} onClick={copy} aria-label={label}>
            {state === "copied" ? <Check aria-hidden="true" className="text-success" /> : <Copy aria-hidden="true" />}
          </Button>
        </TooltipTrigger>
        <TooltipContent>{tip}</TooltipContent>
      </Tooltip>
      <span className="sr-only" role="status" aria-live="polite">
        {state === "copied" ? "Copied to the clipboard." : state === "failed" ? "Could not copy. Select the text and copy it instead." : ""}
      </span>
    </>
  );
}

export { CopyButton };
