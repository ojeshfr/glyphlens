import { Reveal, SectionHeading } from "@/components/reveal";
import { Accordion, AccordionContent, AccordionItem, AccordionTrigger } from "@/components/ui/accordion";
import { release, REPOSITORY } from "@/release";

const FAQ: { q: string; a: React.ReactNode }[] = [
  {
    q: "Does GlyphLens send my screen anywhere?",
    a: (
      <>
        <p>
          No. Frames and recognised text stay in memory on your PC, and OCR and translation run in a local process. Nothing from your
          screen is uploaded, logged or saved.
        </p>
        <p>
          Two things use the network: the one-time setup that you start with Install everything, and the update check, which in the code as
          it stands is not set up and contacts nothing. See{" "}
          <a className="text-link underline underline-offset-4" href="#privacy">
            Privacy
          </a>{" "}
          for what it would send once it is, and for the limits of what has been checked.
        </p>
      </>
    ),
  },
  {
    q: "Which languages does it support?",
    a: (
      <p>
        Simplified Chinese to English and Traditional Chinese to English. Nothing else is offered yet; other languages will be added only
        after they have been tested the same way.
      </p>
    ),
  },
  {
    q: "What does it need?",
    a: (
      <p>
        Windows 11 on a 64-bit PC; a CPU is enough. Nothing has to be installed first: the zip carries its own .NET runtime, and the
        Install everything button downloads a private Python, the engines and the Chinese models once (about 360 MB, about 1 GB of disk).
        It has been tested on one Windows 11 PC so far.
      </p>
    ),
  },
  {
    q: "Is it signed?",
    a: (
      <>
        <p>
          {release.published
            ? "The preview zip is not code-signed (Authenticode). Its SHA-256 is listed in the Download section and on the release page so you can check the file."
            : "No build has been published yet. The first one is planned as an unsigned preview: the installer will not be code-signed (Authenticode), and its SHA-256 will be listed here so you can check the file."}
        </p>
        <p>
          Updates are a separate matter: the update check accepts an update only if it is signed with the release key built into the app,
          and only if the installer matches the SHA-256 in that signed file.
        </p>
      </>
    ),
  },
  {
    q: "Why does Windows warn me about it?",
    a: (
      <p>
        Windows SmartScreen warns about programs that are not code-signed and not yet widely downloaded. Compare the file's SHA-256 with
        the one on this page first. If it matches, choose More info, then Run anyway. If it does not match, delete the file.
      </p>
    ),
  },
  {
    q: "How do updates work?",
    a: (
      <>
        <p>
          Once a release owner sets up a signed update feed, GlyphLens checks it about once a day. It tells you when a new version exists;
          nothing is downloaded until you press Download, and nothing is installed until you press Install and restart.
        </p>
        <p>The check can be turned off in Settings. Until the feed is set up, the check is inactive.</p>
      </>
    ),
  },
  {
    q: "Can I use it commercially? What is the licence?",
    a: (
      <>
        <p>
          GlyphLens is licensed under the{" "}
          <a className="text-link underline underline-offset-4" href={`${REPOSITORY}/blob/main/LICENSE`}>
            Apache License 2.0
          </a>
          : you may use, change and redistribute it, including commercially, if you keep the licence and notices. It includes a patent grant.
          The source is on{" "}
          <a className="text-link underline underline-offset-4" href={REPOSITORY}>
            GitHub
          </a>
          .
        </p>
        <p>
          The licence covers GlyphLens's own code, not the models. The OCR and translation models are licensed separately by their authors
          (the Argos Chinese packages are CC-BY 4.0), and the third-party libraries in the download are listed in THIRD-PARTY-NOTICES.md.
          The licence does not give you the right to use the name "GlyphLens" to suggest endorsement. This is a summary, not legal advice.
        </p>
      </>
    ),
  },
  {
    q: "How good are the translations?",
    a: (
      <p>
        Good enough to follow a conversation; not a human translator. Ordinary sentences and questions mostly come through, idioms and slang
        often do not. The panel keeps the original next to each translation so you can check it.
      </p>
    ),
  },
  {
    q: "Is there a version for macOS or Linux?",
    a: <p>No. The design borrows from Apple's calm style, but GlyphLens is a Windows app built on Windows-only capture and windowing.</p>,
  },
];

export function Faq() {
  return (
    <section id="faq" aria-labelledby="faq-title" className="py-24 sm:py-32">
      <div className="container-page grid grid-cols-[minmax(0,1fr)] gap-10 lg:grid-cols-[minmax(0,4fr)_minmax(0,8fr)] lg:gap-16">
        <SectionHeading id="faq-title" eyebrow="FAQ" title="Questions, answered plainly." />
        <Reveal>
          <Accordion type="single" collapsible>
            {FAQ.map((item, i) => (
              <AccordionItem key={item.q} value={`item-${i}`}>
                <AccordionTrigger>{item.q}</AccordionTrigger>
                <AccordionContent>{item.a}</AccordionContent>
              </AccordionItem>
            ))}
          </Accordion>
        </Reveal>
      </div>
    </section>
  );
}
