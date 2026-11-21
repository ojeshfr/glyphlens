import * as React from "react";
import { Moon, Sun } from "lucide-react";

import { RenderImage } from "@/components/render-image";
import { Reveal, SectionHeading } from "@/components/reveal";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { useTheme, type ResolvedTheme } from "@/lib/theme";
import type { RenderName } from "@/lib/renders";

type View = {
  value: string;
  label: string;
  render: RenderName;
  title: string;
  points: string[];
  alt: string;
  note?: string;
};

const VIEWS: View[] = [
  {
    value: "panel",
    label: "Floating panel",
    render: "overlay",
    title: "The panel is where you read",
    points: [
      "Each message shows the Chinese original small and grey, with the English translation in bold below it.",
      "Copy the original or the translation with separate buttons. New results never take the keyboard focus.",
      "Interactive mode lets you select and copy. Click-through mode passes the mouse to the app underneath; a small handle brings the panel back.",
      "Pin it above other windows and set its opacity and text size.",
      "Windows is asked to leave the panel out of screen captures, so GlyphLens never reads its own translations. Checked on Windows 11 build 26200.",
    ],
    alt: "The floating GlyphLens panel with three Chinese messages and their English translations, copy buttons for original and translation, an opacity slider and Interactive, A−, A+ and Clear controls.",
  },
  {
    value: "capture",
    label: "Capture",
    render: "main-Capture",
    title: "The main window is where you set it up",
    points: [
      "Choose a display or a single window, then start and stop the capture yourself.",
      "Live reading reads the screen again when it changes, usually only the part that changed.",
      "Translation is a switch. The source language is detected automatically, or you choose it; the target is English.",
    ],
    alt: "The GlyphLens Capture page with Start capture and Stop, a Display or Window choice, a preview area, Text recognition with Read text and Live reading, and Translation from Detect automatically to English.",
  },
  {
    value: "settings",
    label: "Settings",
    render: "main-Settings",
    title: "Settings only for things that work",
    points: [
      "Appearance follows Windows, or stays light or dark.",
      "Panel opacity, text size and whether it stays above other windows.",
      "The five shortcuts are fixed in this version; one that another program already owns is reported, never forced.",
      "Updates: see Privacy below for exactly what the update check does.",
    ],
    note: "The update notice in this render is test data. No version of GlyphLens has been released.",
    alt: "The GlyphLens Settings page with Appearance (System, Light, Dark), Overlay appearance (opacity, text size, keep above other windows), Shortcuts and Updates.",
  },
];

export function TheApp() {
  const { resolved } = useTheme();
  const [override, setOverride] = React.useState<ResolvedTheme | null>(null);
  const shotTheme = override ?? resolved;

  return (
    <section id="app" aria-labelledby="app-title" className="py-24 sm:py-32">
      <div className="container-page">
        <SectionHeading id="app-title" eyebrow="The app" title="A small panel to read. A quiet window to set it up.">
          <p>Two windows, shown here as renders of the real app with sample data.</p>
        </SectionHeading>

        <Reveal className="mt-12">
          <Tabs defaultValue="panel">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <TabsList aria-label="Choose a view of the app" className="w-full flex-wrap sm:w-fit">
                {VIEWS.map((v) => (
                  <TabsTrigger key={v.value} value={v.value} className="px-3 sm:px-4">
                    {v.label}
                  </TabsTrigger>
                ))}
              </TabsList>
              <ToggleGroup
                type="single"
                value={shotTheme}
                onValueChange={(value) => value && setOverride(value as ResolvedTheme)}
                aria-label="Appearance of the screenshots"
              >
                <ToggleGroupItem value="light" className="px-4">
                  <Sun aria-hidden="true" />
                  Light
                </ToggleGroupItem>
                <ToggleGroupItem value="dark" className="px-4">
                  <Moon aria-hidden="true" />
                  Dark
                </ToggleGroupItem>
              </ToggleGroup>
            </div>

            {VIEWS.map((v) => (
              <TabsContent key={v.value} value={v.value}>
                <div className="grid items-center gap-10 rounded-[1.75rem] bg-band p-5 sm:p-8 lg:grid-cols-[minmax(0,5fr)_minmax(0,7fr)] lg:gap-14 lg:p-12">
                  <div className="order-2 lg:order-1">
                    <h3 className="text-2xl font-semibold tracking-tight sm:text-[1.75rem]">{v.title}</h3>
                    <ul className="mt-5 flex flex-col gap-3.5">
                      {v.points.map((point) => (
                        <li key={point} className="flex gap-3 text-[0.9875rem] leading-relaxed text-muted-foreground">
                          <span aria-hidden="true" className="mt-[0.6rem] size-1.5 shrink-0 rounded-full bg-link" />
                          <span>{point}</span>
                        </li>
                      ))}
                    </ul>
                    {v.note ? <p className="mt-5 rounded-xl bg-warning-surface px-4 py-3 text-sm text-warning">{v.note}</p> : null}
                  </div>
                  <figure className="order-1 lg:order-2">
                    <div className={v.render === "overlay" ? "mx-auto w-full max-w-[300px]" : "overflow-hidden rounded-xl shadow-panel ring-1 ring-hairline"}>
                      <RenderImage
                        name={v.render}
                        theme={shotTheme}
                        alt={v.alt}
                        className={v.render === "overlay" ? "drop-shadow-xl" : undefined}
                        sizes={v.render === "overlay" ? "300px" : "(min-width: 1024px) 640px, 92vw"}
                      />
                    </div>
                    <figcaption className="sr-only">
                      {v.label}, {shotTheme} appearance.
                    </figcaption>
                  </figure>
                </div>
              </TabsContent>
            ))}
          </Tabs>
        </Reveal>
      </div>
    </section>
  );
}
