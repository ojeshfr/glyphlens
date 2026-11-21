import { ArrowDown, ArrowRight, Copy } from "lucide-react";

import { Reveal, SectionHeading } from "@/components/reveal";
import { cn } from "@/lib/utils";

type Step = {
  n: number;
  verb: string;
  title: string;
  body: string;
  where: "app" | "worker";
  artifact: React.ReactNode;
};

const ORIGINAL = "你今天晚上有空吗？我们一起去吃饭吧。";
const TRANSLATION = "Are you free tonight? Let's eat together.";

function Tag({ children }: { children: React.ReactNode }) {
  return <span className="inline-flex rounded-full bg-secondary px-2.5 py-0.5 text-xs font-medium text-secondary-foreground">{children}</span>;
}

const STEPS: Step[] = [
  {
    n: 1,
    verb: "Capture",
    title: "You choose what to read",
    body: "Pick a window or a display and press Start capture. Windows shows its own capture border while it runs.",
    where: "app",
    artifact: (
      <div className="flex flex-col gap-2">
        <div className="flex flex-wrap gap-1.5">
          <Tag>Windows Graphics Capture</Tag>
        </div>
        <p className="text-sm text-muted-foreground">Frames are kept in memory only, never written to disk.</p>
      </div>
    ),
  },
  {
    n: 2,
    verb: "Read",
    title: "The Chinese is recognised",
    body: "PP-OCRv5 reads the frame on your CPU and returns lines of text. The app joins the lines of one chat bubble into one message.",
    where: "worker",
    artifact: (
      <div className="flex flex-col gap-2">
        <p lang="zh-Hans" className="text-[0.9375rem] leading-relaxed">
          {ORIGINAL}
        </p>
        <div className="flex flex-wrap gap-1.5">
          <Tag>Simplified Chinese</Tag>
        </div>
      </div>
    ),
  },
  {
    n: 3,
    verb: "Translate",
    title: "It becomes English locally",
    body: "An Argos Chinese-to-English model, run with CTranslate2, translates the message. No cloud service is involved.",
    where: "worker",
    artifact: (
      <div className="flex flex-col gap-2">
        <p className="text-[0.9375rem] leading-relaxed font-medium">{TRANSLATION}</p>
        <div className="flex flex-wrap gap-1.5">
          <Tag>zh → en</Tag>
        </div>
      </div>
    ),
  },
  {
    n: 4,
    verb: "Show",
    title: "The panel shows both",
    body: "The original stays above the translation, so you can check one against the other and copy either.",
    where: "app",
    artifact: (
      <div className="rounded-xl bg-secondary p-3">
        <p lang="zh-Hans" className="text-[0.8125rem] text-muted-foreground">
          {ORIGINAL}
        </p>
        <p className="mt-1 text-sm font-semibold">{TRANSLATION}</p>
        <p className="mt-2 flex gap-3 text-xs font-medium text-link" aria-hidden="true">
          <span className="inline-flex items-center gap-1">
            <Copy className="size-3" /> Original
          </span>
          <span className="inline-flex items-center gap-1">
            <Copy className="size-3" /> Translation
          </span>
        </p>
      </div>
    ),
  },
];

function StepCard({ step, wide = false }: { step: Step; wide?: boolean }) {
  return (
    <li
      className={cn(
        "flex flex-col gap-4 rounded-2xl bg-card-raised p-5 sm:p-6",
        // Outside the worker box, below the widest layout, the card puts its example beside the text.
        wide && "md:grid md:grid-cols-2 md:gap-x-8 xl:flex",
      )}
    >
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <span className="inline-flex size-8 items-center justify-center rounded-full bg-primary text-sm font-semibold text-primary-foreground" aria-hidden="true">
          {step.n}
        </span>
        <span className="text-sm font-semibold text-link">{step.verb}</span>
        <span className="ml-auto text-xs whitespace-nowrap text-muted-foreground">{step.where === "app" ? "in the app" : "in the worker"}</span>
      </div>
      <div className={cn(wide && "md:col-start-1")}>
        <h3 className="text-lg font-semibold tracking-tight">
          <span className="sr-only">Step {step.n}: </span>
          {step.title}
        </h3>
        <p className="mt-1.5 text-[0.9375rem] leading-relaxed text-muted-foreground">{step.body}</p>
      </div>
      <div className={cn("mt-auto border-t border-hairline pt-4", wide && "md:col-start-2 md:row-span-2 md:row-start-1 md:mt-0 md:self-center md:border-t-0 md:pt-0 xl:mt-auto xl:self-auto xl:border-t xl:pt-4")}>
        {step.artifact}
      </div>
    </li>
  );
}

/** Arrow between steps: pointing down while the diagram is stacked, right in the widest layout. */
function Connector() {
  return (
    <div aria-hidden="true" className="flex items-center justify-center text-muted-foreground">
      <ArrowDown className="size-5 xl:hidden" />
      <ArrowRight className="hidden size-5 xl:block" />
    </div>
  );
}

export function HowItWorks() {
  return (
    <section id="how" aria-labelledby="how-title" className="bg-band py-24 sm:py-32">
      <div className="container-page">
        <SectionHeading id="how-title" eyebrow="How it works" title="Four steps. All of them on your PC.">
          <p>
            One message, followed from the screen to the panel. The example sentence and its translation come from GlyphLens' own
            end-to-end test.
          </p>
        </SectionHeading>

        <Reveal className="mt-14">
          {/* Diagram: the middle two steps run in a separate local worker process, drawn as a dashed region. */}
          <div className="grid grid-cols-[minmax(0,1fr)] gap-3 xl:grid-cols-[minmax(0,1fr)_auto_minmax(0,2fr)_auto_minmax(0,1fr)] xl:items-stretch xl:gap-4">
            <ol className="grid">
              <StepCard step={STEPS[0]!} wide />
            </ol>
            <Connector />
            <div className="rounded-[1.25rem] border-2 border-dashed border-border p-2 sm:p-3">
              <p className="px-2 pt-1 pb-3 text-sm text-muted-foreground">
                <span className="font-semibold text-foreground">Local worker process</span>, started by the app and reached over a named
                pipe that only your Windows user can open.
              </p>
              <ol start={2} className="grid gap-3 md:grid-cols-2">
                <StepCard step={STEPS[1]!} />
                <StepCard step={STEPS[2]!} />
              </ol>
            </div>
            <Connector />
            <ol start={4} className="grid">
              <StepCard step={STEPS[3]!} wide />
            </ol>
          </div>
        </Reveal>
      </div>
    </section>
  );
}
