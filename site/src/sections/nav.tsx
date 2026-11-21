import * as React from "react";
import { Menu, Monitor, Moon, Sun } from "lucide-react";

import { Button } from "@/components/ui/button";
import { Separator } from "@/components/ui/separator";
import { Sheet, SheetClose, SheetContent, SheetDescription, SheetTitle, SheetTrigger } from "@/components/ui/sheet";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { useTheme, type ThemeChoice } from "@/lib/theme";
import { cn } from "@/lib/utils";

export const NAV_LINKS = [
  { href: "#how", label: "How it works" },
  { href: "#app", label: "The app" },
  { href: "#languages", label: "Languages" },
  { href: "#privacy", label: "Privacy" },
  { href: "#download", label: "Download" },
  { href: "#faq", label: "FAQ" },
] as const;

const THEME_OPTIONS: { value: ThemeChoice; label: string; Icon: typeof Sun }[] = [
  { value: "system", label: "System", Icon: Monitor },
  { value: "light", label: "Light", Icon: Sun },
  { value: "dark", label: "Dark", Icon: Moon },
];

export function Logo({ className }: { className?: string }) {
  return (
    <span className={cn("inline-flex size-7 items-center justify-center rounded-[0.45rem] bg-primary text-[0.95rem] font-bold text-primary-foreground", className)} aria-hidden="true">
      G
    </span>
  );
}

/** System / Light / Dark. `compact` shows icons only (with tooltips and accessible names). */
function ThemeControl({ compact }: { compact: boolean }) {
  const { choice, setChoice } = useTheme();
  return (
    <ToggleGroup
      type="single"
      value={choice}
      onValueChange={(value) => value && setChoice(value as ThemeChoice)}
      aria-label="Page appearance"
      className={compact ? "" : "w-full"}
    >
      {THEME_OPTIONS.map(({ value, label, Icon }) =>
        compact ? (
          <Tooltip key={value}>
            <TooltipTrigger asChild>
              <ToggleGroupItem value={value} aria-label={label} className="min-h-9 min-w-9 px-2">
                <Icon aria-hidden="true" />
              </ToggleGroupItem>
            </TooltipTrigger>
            <TooltipContent>{value === "system" ? "Follow the system" : label}</TooltipContent>
          </Tooltip>
        ) : (
          <ToggleGroupItem key={value} value={value} className="flex-1">
            <Icon aria-hidden="true" />
            {label}
          </ToggleGroupItem>
        ),
      )}
    </ToggleGroup>
  );
}

export function Nav() {
  const [open, setOpen] = React.useState(false);
  const firstLink = React.useRef<HTMLAnchorElement>(null);
  return (
    <header className="sticky top-0 z-30 border-b border-hairline bg-nav backdrop-blur-xl backdrop-saturate-150">
      <div className="container-page flex h-14 items-center gap-4">
        <a href="#top" className="flex min-h-11 items-center gap-2.5 rounded-md text-[1.0625rem] font-semibold tracking-tight outline-hidden focus-visible:outline-2 focus-visible:outline-solid focus-visible:outline-offset-2 focus-visible:outline-ring">
          <Logo />
          GlyphLens
        </a>
        <nav aria-label="Sections" className="ml-auto hidden lg:block">
          <ul className="flex items-center gap-1">
            {NAV_LINKS.map((link) => (
              <li key={link.href}>
                <a
                  href={link.href}
                  className="inline-flex min-h-9 items-center rounded-full px-3 text-sm text-muted-foreground transition-colors outline-hidden hover:text-foreground focus-visible:outline-2 focus-visible:outline-solid focus-visible:outline-offset-1 focus-visible:outline-ring"
                >
                  {link.label}
                </a>
              </li>
            ))}
          </ul>
        </nav>
        <div className="hidden lg:block">
          <ThemeControl compact />
        </div>
        <Sheet open={open} onOpenChange={setOpen}>
          <SheetTrigger asChild>
            <Button variant="ghost" size="icon" className="ml-auto lg:hidden" aria-label="Open menu">
              <Menu aria-hidden="true" className="size-5" />
            </Button>
          </SheetTrigger>
          <SheetContent
            aria-describedby="mobile-menu-desc"
            onOpenAutoFocus={(event) => {
              // Start on the first section link (by default the focus would land on the first focusable element in DOM order).
              event.preventDefault();
              firstLink.current?.focus();
            }}
          >
            <div className="flex min-h-11 items-center gap-2.5 pr-12">
              <Logo />
              <SheetTitle>GlyphLens</SheetTitle>
            </div>
            <SheetDescription id="mobile-menu-desc" className="sr-only">
              Sections of this page and the appearance setting.
            </SheetDescription>
            <nav aria-label="Sections">
              <ul className="flex flex-col">
                {NAV_LINKS.map((link, i) => (
                  <li key={link.href}>
                    <SheetClose asChild>
                      <a
                        ref={i === 0 ? firstLink : undefined}
                        href={link.href}
                        className="flex min-h-12 items-center rounded-lg px-2 text-lg font-medium outline-hidden hover:bg-secondary focus-visible:outline-2 focus-visible:outline-solid focus-visible:outline-offset-0 focus-visible:outline-ring"
                      >
                        {link.label}
                      </a>
                    </SheetClose>
                  </li>
                ))}
              </ul>
            </nav>
            <Separator className="my-2" />
            <p className="px-2 text-sm text-muted-foreground" aria-hidden="true">
              Appearance
            </p>
            <ThemeControl compact={false} />
          </SheetContent>
        </Sheet>
      </div>
    </header>
  );
}
