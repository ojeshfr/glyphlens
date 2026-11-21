import * as React from "react";
import { ArrowDown, ChevronRight } from "lucide-react";
import { m, useReducedMotion, useScroll, useTransform } from "motion/react";

import { RenderImage } from "@/components/render-image";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { release } from "@/release";
import { useTheme } from "@/lib/theme";

function HeroVisual() {
  const { resolved } = useTheme();
  const reduce = useReducedMotion();
  const ref = React.useRef<HTMLDivElement>(null);
  const { scrollYProgress } = useScroll({ target: ref, offset: ["start end", "end start"] });
  // Subtle: the window settles from 94 % to full size as it scrolls up; the panel drifts a little faster than the window.
  const windowScale = useTransform(scrollYProgress, [0, 0.45], [0.94, 1]);
  const panelY = useTransform(scrollYProgress, [0, 1], [48, -48]);

  return (
    <div ref={ref} className="relative mx-auto mt-14 w-full max-w-5xl sm:mt-20">
      <m.div style={reduce ? undefined : { scale: windowScale }} className="w-[88%] origin-bottom overflow-hidden rounded-xl shadow-panel ring-1 ring-hairline sm:w-[80%]">
        <RenderImage
          name="main-Capture"
          theme={resolved}
          priority
          sizes="(min-width: 1024px) 820px, 88vw"
          alt="The GlyphLens main window on its Capture page: Start capture, a Display or Window choice, a preview area, Text recognition and Translation settings."
        />
      </m.div>
      <m.div
        style={reduce ? undefined : { y: panelY }}
        className="absolute right-0 bottom-[-6%] w-[40%] max-w-[300px] min-w-[132px] sm:right-[2%] sm:w-[33%]"
      >
        <RenderImage
          name="overlay"
          theme={resolved}
          priority
          sizes="(min-width: 1024px) 300px, 40vw"
          className="drop-shadow-2xl"
          alt="The floating GlyphLens panel above the main window, showing three Chinese chat messages, each with its English translation and buttons to copy the original or the translation."
        />
      </m.div>
    </div>
  );
}

export function Hero() {
  return (
    <section id="top" aria-labelledby="hero-title" className="overflow-x-clip pt-14 pb-24 sm:pt-20 sm:pb-32">
      <div className="container-page flex flex-col items-center text-center">
        <Badge variant="outline">{release.published ? `${release.version} · Windows 11 · developer preview` : "Early preview · Windows 11 · not released yet"}</Badge>
        <h1 id="hero-title" className="mt-6 max-w-4xl text-[2.6rem] leading-[1.04] font-bold tracking-[-0.035em] text-balance sm:text-6xl lg:text-7xl">
          Read Chinese on your screen in English.
          <span className="block text-muted-foreground">Without it leaving your PC.</span>
        </h1>
        <p className="mt-6 max-w-2xl text-lg leading-relaxed text-pretty text-muted-foreground sm:text-xl">
          GlyphLens reads the text in a window or on a display with a local OCR, translates it on your computer and shows the
          English in a small panel that floats above your other apps. Simplified and Traditional Chinese.
        </p>
        <div className="mt-8 flex flex-col items-center gap-3 sm:flex-row sm:gap-4">
          <Button asChild size="lg">
            <a href="#how">
              See how it works
              <ArrowDown aria-hidden="true" />
            </a>
          </Button>
          <Button asChild variant="link" size="lg" className="px-2">
            <a href="#download">
              {release.published ? `Download ${release.version}` : "Release status"}
              <ChevronRight aria-hidden="true" />
            </a>
          </Button>
        </div>
      </div>
      <div className="container-page">
        <HeroVisual />
        <p className="mx-auto mt-14 max-w-xl text-center text-sm text-muted-foreground sm:mt-16">
          Renders of the real app with sample data: the panel floating above the main window.
        </p>
      </div>
    </section>
  );
}
