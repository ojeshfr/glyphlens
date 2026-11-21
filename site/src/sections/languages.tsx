import { Hand, MousePointerClick } from "lucide-react";

import { Reveal, SectionHeading } from "@/components/reveal";
import { Card, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Kbd, KbdGroup } from "@/components/ui/kbd";
import { Separator } from "@/components/ui/separator";

const SHORTCUTS: { key: string; action: string }[] = [
  { key: "T", action: "Turn translation on or off" },
  { key: "O", action: "Show or hide the panel" },
  { key: "P", action: "Pause or resume" },
  { key: "I", action: "Switch between interactive and click-through" },
  { key: "C", action: "Clear the results" },
];

const STACK: { term: string; detail: string }[] = [
  { term: "App", detail: "C#, .NET 10, WPF" },
  { term: "Screen capture", detail: "Windows Graphics Capture" },
  { term: "OCR", detail: "PaddleOCR PP-OCRv5 mobile on ONNX Runtime, CPU" },
  { term: "Translation", detail: "Argos Translate models (OPUS-MT), run directly with CTranslate2 and SentencePiece" },
  { term: "Worker", detail: "Python, a child process of the app, over a local named pipe with a per-session token" },
  { term: "GPU", detail: "Not used" },
];

const MEASURED: { value: string; label: string }[] = [
  { value: "1.2 %", label: "character error rate of the OCR on 10 test windows" },
  { value: "0.4–1.3 s", label: "to read one window" },
  { value: "72 ms", label: "median per translated sentence (Simplified)" },
  { value: "142 ms", label: "median Live pass, against 733 ms when every frame is read in full" },
];

export function Languages() {
  return (
    <section id="languages" aria-labelledby="languages-title" className="bg-band py-24 sm:py-32">
      <div className="container-page">
        <SectionHeading id="languages-title" eyebrow="Languages and build" title="Chinese first. English out.">
          <p>One direction, tested end to end, before anything else is added.</p>
        </SectionHeading>

        <div className="mt-14 grid grid-cols-[minmax(0,1fr)] gap-4 lg:grid-cols-[repeat(2,minmax(0,1fr))]">
          <Reveal>
            <Card className="h-full bg-card-raised">
              <CardHeader>
                <CardTitle>What it translates</CardTitle>
              </CardHeader>
              <table className="w-full text-left text-[0.9375rem]">
                <caption className="sr-only">Supported translation directions</caption>
                <thead>
                  <tr className="text-sm text-muted-foreground">
                    <th scope="col" className="pb-2 font-medium">From</th>
                    <th scope="col" className="pb-2 font-medium">To</th>
                    <th scope="col" className="pb-2 font-medium">Model</th>
                  </tr>
                </thead>
                <tbody className="align-top">
                  <tr className="border-t border-hairline">
                    <th scope="row" className="py-3 pr-3 font-semibold">
                      Simplified Chinese <span lang="zh-Hans" className="block text-sm font-normal text-muted-foreground">简体中文</span>
                    </th>
                    <td className="py-3 pr-3">English</td>
                    <td className="py-3 sm:whitespace-nowrap">Argos zh→en 1.9</td>
                  </tr>
                  <tr className="border-t border-hairline">
                    <th scope="row" className="py-3 pr-3 font-semibold">
                      Traditional Chinese <span lang="zh-Hant" className="block text-sm font-normal text-muted-foreground">繁體中文</span>
                    </th>
                    <td className="py-3 pr-3">English</td>
                    <td className="py-3 sm:whitespace-nowrap">Argos zt→en 1.9</td>
                  </tr>
                </tbody>
              </table>
              <CardDescription>
                Both packages carry the same model weights; each direction passed its own end-to-end test. Whether text is Simplified or
                Traditional is decided from characters that exist in only one of the two character sets (GB2312 or Big5). Other languages are not offered until they
                have been tested.
              </CardDescription>
            </Card>
          </Reveal>

          <Reveal delay={0.05}>
            <Card className="h-full bg-card-raised">
              <CardHeader>
                <CardTitle>How good the translations are</CardTitle>
                <CardDescription>
                  Good enough to follow a conversation, not a human translator. On a 52-sentence test set written for this project, the
                  Simplified model produced 16 correct, 19 acceptable, 11 partial and 5 wrong translations; one invented output was caught and
                  dropped.
                </CardDescription>
              </CardHeader>
              <CardDescription>
                Ordinary sentences, questions and numbers mostly come through. Idioms, slang and sarcasm often do not. That is why the panel
                always keeps the original next to the translation.
              </CardDescription>
              <p className="mt-auto text-sm text-muted-foreground">
                The test sentences and the judgement of the outputs are the project's own, not an independent review.
              </p>
            </Card>
          </Reveal>

          <Reveal>
            <Card className="h-full bg-card-raised">
              <CardHeader>
                <CardTitle>Five shortcuts, from any app</CardTitle>
                <CardDescription>
                  They work while another program is in front. A shortcut that is already taken is reported, never forced. They use
                  RegisterHotKey, so GlyphLens sees no other keystrokes.
                </CardDescription>
              </CardHeader>
              <dl className="flex flex-col">
                {SHORTCUTS.map((s) => (
                  <div key={s.key} className="flex flex-col gap-1.5 border-t border-hairline py-3 sm:flex-row sm:items-center sm:gap-4">
                    <dt className="shrink-0 sm:w-36">
                      <KbdGroup aria-label={`Control Alt ${s.key}`}>
                        <Kbd aria-hidden="true">Ctrl</Kbd>
                        <Kbd aria-hidden="true">Alt</Kbd>
                        <Kbd aria-hidden="true">{s.key}</Kbd>
                      </KbdGroup>
                    </dt>
                    <dd className="text-[0.9375rem]">{s.action}</dd>
                  </div>
                ))}
              </dl>
            </Card>
          </Reveal>

          <Reveal delay={0.05}>
            <Card className="h-full bg-card-raised">
              <CardHeader>
                <CardTitle>Two ways for the panel to behave</CardTitle>
              </CardHeader>
              <div className="grid gap-4 sm:grid-cols-2">
                <div className="rounded-xl bg-band p-4">
                  <MousePointerClick aria-hidden="true" className="size-5 text-link" />
                  <h4 className="mt-3 font-semibold">Interactive</h4>
                  <p className="mt-1 text-[0.9375rem] leading-relaxed text-muted-foreground">
                    The panel takes the mouse: scroll, select and copy text, drag and resize it.
                  </p>
                </div>
                <div className="rounded-xl bg-band p-4">
                  <Hand aria-hidden="true" className="size-5 text-link" />
                  <h4 className="mt-3 font-semibold">Click-through</h4>
                  <p className="mt-1 text-[0.9375rem] leading-relaxed text-muted-foreground">
                    Clicks pass to the app underneath while you keep reading. A small handle, or Ctrl+Alt+I, switches back.
                  </p>
                </div>
              </div>
              <Separator />
              <div>
                <h4 className="font-semibold">Built with</h4>
                <dl className="mt-3 grid gap-x-4 gap-y-2 text-[0.9375rem] sm:grid-cols-[auto_1fr]">
                  {STACK.map((row) => (
                    <div key={row.term} className="contents">
                      <dt className="text-muted-foreground">{row.term}</dt>
                      <dd className="mb-2 sm:mb-0">{row.detail}</dd>
                    </div>
                  ))}
                </dl>
              </div>
            </Card>
          </Reveal>
        </div>

        <Reveal className="mt-4">
          <Card className="bg-card-raised">
            <CardHeader>
              <CardTitle>Measured on one PC</CardTitle>
              <CardDescription>
                Intel Core i7-12700H, CPU only, Windows 11 build 26200; one run each. The OCR test windows were drawn by a test app, not real
                chat apps. Measurements, not benchmarks.
              </CardDescription>
            </CardHeader>
            <dl className="grid gap-6 sm:grid-cols-2 lg:grid-cols-4">
              {MEASURED.map((m) => (
                <div key={m.label} className="flex flex-col-reverse justify-end gap-1 border-t border-hairline pt-4">
                  <dt className="text-sm leading-snug text-muted-foreground">{m.label}</dt>
                  <dd className="text-3xl font-semibold tracking-tight">{m.value}</dd>
                </div>
              ))}
            </dl>
          </Card>
        </Reveal>
      </div>
    </section>
  );
}
