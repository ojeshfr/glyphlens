import { CircleCheck, CircleHelp, Globe, HardDrive } from "lucide-react";

import { Reveal, SectionHeading } from "@/components/reveal";
import { Card, CardHeader, CardTitle } from "@/components/ui/card";

function Points({ items }: { items: React.ReactNode[] }) {
  return (
    <ul className="flex flex-col gap-3.5">
      {items.map((item, i) => (
        <li key={i} className="flex gap-3 text-[0.9875rem] leading-relaxed text-muted-foreground">
          <span aria-hidden="true" className="mt-[0.6rem] size-1.5 shrink-0 rounded-full bg-link" />
          <span>{item}</span>
        </li>
      ))}
    </ul>
  );
}

export function Privacy() {
  return (
    <section id="privacy" aria-labelledby="privacy-title" className="py-24 sm:py-32">
      <div className="container-page">
        <SectionHeading id="privacy-title" eyebrow="Privacy" title="Your screen stays on your PC. Here is exactly what that means.">
          <p>What GlyphLens keeps, the one feature that uses the network, and what has been checked so far.</p>
        </SectionHeading>

        <div className="mt-14 grid grid-cols-[minmax(0,1fr)] gap-4 lg:grid-cols-[repeat(2,minmax(0,1fr))]">
          <Reveal>
            <Card className="h-full">
              <CardHeader className="flex-row items-center gap-3">
                <HardDrive aria-hidden="true" className="size-6 text-link" />
                <CardTitle>What stays on your PC</CardTitle>
              </CardHeader>
              <Points
                items={[
                  "Captured frames and recognised text are held in memory only. They are never written to disk, logged or uploaded.",
                  "OCR and translation run in a local worker process. There is no cloud translation and no silent fallback to one: if a model is missing, GlyphLens says so.",
                  "Models are files you add yourself. GlyphLens never downloads a model.",
                  "On disk it keeps only the panel's position, size, opacity and text size, your appearance choice and the update settings.",
                  "The clipboard is used only when you press a Copy button. There is no account, no telemetry and no analytics.",
                ]}
              />
            </Card>
          </Reveal>

          <Reveal delay={0.05}>
            <Card className="h-full">
              <CardHeader className="flex-row items-center gap-3">
                <Globe aria-hidden="true" className="size-6 text-link" />
                <CardTitle>What uses the network: the update check</CardTitle>
              </CardHeader>
              <Points
                items={[
                  <>
                    GlyphLens contains an automatic update check, on by default. <strong className="font-semibold text-foreground">In the code as it
                    stands, no feed address or signing key is built in, so the check does nothing and contacts nothing.</strong>
                  </>,
                  "Once a release owner sets it up, it requests one small signed file over HTTPS about once a day, with a GlyphLens/version User-Agent, no cookies and no identifier. That server sees your IP address and which version you run; nothing from your screen, no text and no settings are sent.",
                  "An update is downloaded only when you press Download, kept only if it matches the SHA-256 in the signed file, and run only when you press Install and restart.",
                  "You can turn it off: Settings, Updates, “Check for updates automatically”.",
                ]}
              />
            </Card>
          </Reveal>
        </div>

        <Reveal className="mt-4">
          <div className="grid grid-cols-[minmax(0,1fr)] gap-8 rounded-2xl border border-border p-6 sm:p-8 lg:grid-cols-[repeat(2,minmax(0,1fr))] lg:gap-12">
            <div>
              <h3 className="flex items-center gap-2.5 text-lg font-semibold tracking-tight">
                <CircleCheck aria-hidden="true" className="size-5 text-success" />
                Checked on one PC
              </h3>
              <div className="mt-4">
                <Points
                  items={[
                    "During 61 seconds of Live reading and translation, 28 samples of the connection tables found no remote TCP connection and no UDP endpoint for the app and all of its worker processes.",
                    "The worker's network guard refuses Python-level connections and recorded none in the evaluation runs.",
                    "The app reaches its worker over a named pipe restricted to your Windows user, not over a network socket.",
                    "The Overview page can list GlyphLens' own network connections whenever you ask.",
                  ]}
                />
              </div>
            </div>
            <div>
              <h3 className="flex items-center gap-2.5 text-lg font-semibold tracking-tight">
                <CircleHelp aria-hidden="true" className="size-5 text-warning" />
                Not proven
              </h3>
              <div className="mt-4">
                <Points
                  items={[
                    "Sampling can miss a connection that opens and closes between two samples.",
                    "The Python guard cannot see connections opened by native libraries (CTranslate2, ONNX Runtime).",
                    "No packet capture, firewall-blocked run or run with the network adapter off has been done.",
                    "Model packages carry no publisher signature or checksum, so importing one is a decision to trust its source.",
                    "None of this protects against malware already running as your Windows user.",
                  ]}
                />
              </div>
            </div>
          </div>
        </Reveal>
      </div>
    </section>
  );
}
