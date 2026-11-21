import { LazyMotion, MotionConfig } from "motion/react";

import { TooltipProvider } from "@/components/ui/tooltip";
import { ThemeProvider } from "@/lib/theme";
import { DownloadSection } from "@/sections/download";
import { Faq } from "@/sections/faq";
import { Footer } from "@/sections/footer";
import { Hero } from "@/sections/hero";
import { HowItWorks } from "@/sections/how-it-works";
import { Languages } from "@/sections/languages";
import { Nav } from "@/sections/nav";
import { Privacy } from "@/sections/privacy";
import { TheApp } from "@/sections/the-app";

const loadFeatures = () => import("@/lib/motion-features").then((mod) => mod.default);

export function App() {
  return (
    <ThemeProvider>
      <LazyMotion features={loadFeatures} strict>
        <MotionConfig reducedMotion="user">
          <TooltipProvider>
            <a
              href="#main"
              className="sr-only z-50 rounded-full bg-primary px-5 py-3 font-medium text-primary-foreground focus:not-sr-only focus:fixed focus:top-3 focus:left-3"
            >
              Skip to content
            </a>
            <Nav />
            <main id="main" tabIndex={-1} className="outline-hidden">
              <Hero />
              <HowItWorks />
              <TheApp />
              <Languages />
              <Privacy />
              <DownloadSection />
              <Faq />
            </main>
            <Footer />
          </TooltipProvider>
        </MotionConfig>
      </LazyMotion>
    </ThemeProvider>
  );
}
