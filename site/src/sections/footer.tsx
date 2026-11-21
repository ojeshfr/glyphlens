import { Separator } from "@/components/ui/separator";
import { release, REPOSITORY } from "@/release";
import { Logo, NAV_LINKS } from "./nav";

export function Footer() {
  return (
    <footer className="border-t border-hairline bg-band py-12 text-sm text-muted-foreground">
      <div className="container-page flex flex-col gap-8">
        <div className="flex flex-col gap-6 md:flex-row md:items-start md:justify-between">
          <div className="flex flex-col gap-2">
            <span className="flex items-center gap-2.5 text-base font-semibold text-foreground">
              <Logo />
              GlyphLens
            </span>
            <p>{release.published ? "Offline screen translation for Windows. Developer preview." : "Offline screen translation for Windows. Early preview, not released yet."}</p>
          </div>
          <nav aria-label="Footer">
            <ul className="flex flex-wrap gap-x-5 gap-y-1">
              {NAV_LINKS.map((link) => (
                <li key={link.href}>
                  <a className="inline-flex min-h-11 min-w-11 items-center rounded-sm outline-hidden hover:text-foreground focus-visible:outline-2 focus-visible:outline-solid focus-visible:outline-ring" href={link.href}>
                    {link.label}
                  </a>
                </li>
              ))}
              <li>
                <a className="inline-flex min-h-11 min-w-11 items-center rounded-sm outline-hidden hover:text-foreground focus-visible:outline-2 focus-visible:outline-solid focus-visible:outline-ring" href={REPOSITORY}>
                  Source on GitHub
                </a>
              </li>
              <li>
                <a className="inline-flex min-h-11 min-w-11 items-center rounded-sm outline-hidden hover:text-foreground focus-visible:outline-2 focus-visible:outline-solid focus-visible:outline-ring" href="THIRD-PARTY-LICENSES.txt">
                  Open-source notices
                </a>
              </li>
            </ul>
          </nav>
        </div>
        <Separator />
        <div className="flex flex-col gap-2 leading-relaxed">
          <p>
            GlyphLens is licensed under the Apache License 2.0; the source is on GitHub. The translation and OCR models are separate and
            keep their own licences. Screens on this page are renders of the app with sample data.
          </p>
          <p>
            This page loads nothing from other servers and sets no cookies. It stores your appearance choice in your browser's local storage,
            and nothing else.
          </p>
          <p>Page updated 21 November 2026.</p>
        </div>
      </div>
    </footer>
  );
}
