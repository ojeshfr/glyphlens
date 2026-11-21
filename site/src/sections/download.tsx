import { Download as DownloadIcon, ExternalLink, ShieldAlert } from "lucide-react";

import { Reveal, SectionHeading } from "@/components/reveal";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardDescription, CardFooter, CardHeader, CardTitle } from "@/components/ui/card";
import { CopyButton } from "@/components/ui/copy-button";
import { Separator } from "@/components/ui/separator";
import { formatBytes, formatIsoDate } from "@/lib/utils";
import { release, RELEASES_PAGE, type Release } from "@/release";

type Published = Extract<Release, { published: true }>;

function CodeLine({ label, value, copyLabel }: { label: string; value: string; copyLabel: string }) {
  return (
    <div>
      <p className="text-sm font-medium">{label}</p>
      <div className="mt-2 flex items-center gap-2 rounded-xl bg-code p-1.5 pl-4">
        <code className="min-w-0 flex-1 font-mono text-[0.8125rem] leading-relaxed wrap-anywhere">{value}</code>
        <CopyButton value={value} label={copyLabel} />
      </div>
    </div>
  );
}

function SmartScreenNote() {
  return (
    <div className="flex gap-3 rounded-xl bg-warning-surface p-4 text-[0.9375rem] leading-relaxed">
      <ShieldAlert aria-hidden="true" className="mt-0.5 size-5 shrink-0 text-warning" />
      <div>
        <p className="font-semibold text-warning">Windows will warn you. That is expected.</p>
        <p className="mt-1 text-foreground">
          This build is not code-signed, so Windows SmartScreen shows "Windows protected your PC". Check the SHA-256 first. If it
          matches, choose More info, then Run anyway. If it does not match, delete the file and do not run it.
        </p>
      </div>
    </div>
  );
}

function PublishedCard({ r }: { r: Published }) {
  return (
    <Card className="h-full bg-card-raised">
      <CardHeader>
        <div className="flex flex-wrap items-center gap-2">
          <Badge variant="outline">Preview</Badge>
          <Badge variant="warning">Not code-signed</Badge>
        </div>
        <CardTitle className="text-2xl sm:text-3xl">GlyphLens {r.version}</CardTitle>
        <CardDescription>
          {r.fileName} · {formatBytes(r.sizeBytes)} · published {formatIsoDate(r.publishedOn)} · Windows 11, 64-bit
        </CardDescription>
        {r.notes ? <p className="text-[0.9375rem] leading-relaxed">{r.notes}</p> : null}
      </CardHeader>
      <CardFooter>
        <Button asChild size="lg">
          <a href={r.url}>
            <DownloadIcon aria-hidden="true" />
            Download for Windows
          </a>
        </Button>
        <Button asChild variant="link" className="px-2">
          <a href={r.notesUrl}>
            Release notes
            <ExternalLink aria-hidden="true" />
            <span className="sr-only">(GitHub)</span>
          </a>
        </Button>
      </CardFooter>
      <Separator />
      <div className="flex flex-col gap-5">
        <h4 className="text-lg font-semibold tracking-tight">Verify the download</h4>
        <CodeLine label="SHA-256 of this file" value={r.sha256} copyLabel="Copy the SHA-256" />
        <CodeLine
          label="In PowerShell, in the folder you saved it to, run"
          value={`Get-FileHash -Algorithm SHA256 .\\${r.fileName}`}
          copyLabel="Copy the PowerShell command"
        />
        <p className="text-[0.9375rem] leading-relaxed text-muted-foreground">
          The Hash it prints must be the same as the SHA-256 above (upper or lower case does not matter).
        </p>
        <div>
          <h4 className="text-lg font-semibold tracking-tight">After you unzip</h4>
          <p className="mt-2 text-[0.9375rem] leading-relaxed text-muted-foreground">
            Open README-PREVIEW.txt. It lists the one-time set-up: the .NET 10 Desktop Runtime, Python with the engine libraries, and the
            models. Then start GlyphLens.exe; the Overview page shows anything that is still missing.
          </p>
        </div>
        <SmartScreenNote />
      </div>
    </Card>
  );
}

function NotPublishedCard() {
  return (
    <Card className="h-full bg-card-raised">
      <CardHeader>
        <Badge variant="warning">Not published yet</Badge>
        <CardTitle className="text-2xl sm:text-3xl">There is no build to download yet</CardTitle>
        <CardDescription>
          GlyphLens is an early preview. The first build has not been published. When it is, it will appear here, with its size and
          SHA-256 so you can check it, and on the repository's Releases page.
        </CardDescription>
      </CardHeader>
      <div>
        <h4 className="font-semibold">What the first build is planned to be</h4>
        <ul className="mt-3 flex flex-col gap-2.5 text-[0.9375rem] leading-relaxed text-muted-foreground">
          <li className="flex gap-3">
            <span aria-hidden="true" className="mt-[0.6rem] size-1.5 shrink-0 rounded-full bg-link" />
            For Windows 11, 64-bit (x64).
          </li>
          <li className="flex gap-3">
            <span aria-hidden="true" className="mt-[0.6rem] size-1.5 shrink-0 rounded-full bg-link" />
            An unsigned preview: Windows SmartScreen will warn before it runs.
          </li>
          <li className="flex gap-3">
            <span aria-hidden="true" className="mt-[0.6rem] size-1.5 shrink-0 rounded-full bg-link" />
            Without models: the OCR and translation models are downloaded and imported separately.
          </li>
        </ul>
      </div>
      <CardFooter className="mt-auto">
        <Button asChild variant="secondary">
          <a href={RELEASES_PAGE}>
            Releases on GitHub
            <ExternalLink aria-hidden="true" />
          </a>
        </Button>
      </CardFooter>
    </Card>
  );
}

const REQUIREMENTS: { title: string; body: string }[] = [
  {
    title: "Windows 11, 64-bit",
    body: "Tested on build 26200, on one PC with one monitor at 150 % scale. Windows 10, other scales and several monitors are not tested.",
  },
  {
    title: "A CPU is enough",
    body: "No GPU is used. In one measurement the worker grew from 45 MB to 478 MB of memory once the OCR models were loaded.",
  },
  {
    title: "Two models you get yourself",
    body: "The PP-OCRv5 mobile OCR files, and the Argos zh→en or zt→en package (74.5 MB, CC-BY 4.0). GlyphLens tells you which one is missing and never fetches them.",
  },
  {
    title: "The .NET 10 Desktop Runtime",
    body: "The preview is a framework-dependent build, so Microsoft's .NET 10 Desktop Runtime (x64) must be installed first.",
  },
  {
    title: "Python for the worker",
    body: "OCR and translation run in a Python 3.11+ worker. The zip contains the worker's source; you install Python and the engine libraries with pip (exact versions are in README-PREVIEW.txt).",
  },
];

export function DownloadSection() {
  return (
    <section id="download" aria-labelledby="download-title" className="bg-band py-24 sm:py-32">
      <div className="container-page">
        <SectionHeading id="download-title" eyebrow="Download" title={release.published ? "Get the preview." : "Not released yet."}>
          <p>
            {release.published
              ? "A developer preview for Windows 11: an unsigned zip, not an installer. Check the file before you run it."
              : "Here is where it stands, and what the first build will need."}
          </p>
        </SectionHeading>

        <div className="mt-14 grid grid-cols-[minmax(0,1fr)] gap-4 lg:grid-cols-[minmax(0,7fr)_minmax(0,5fr)]">
          <Reveal className="h-full">{release.published ? <PublishedCard r={release} /> : <NotPublishedCard />}</Reveal>
          <Reveal delay={0.05} className="h-full">
            <Card id="requirements" className="h-full bg-card-raised" aria-labelledby="requirements-title">
              <CardHeader>
                <CardTitle id="requirements-title">System requirements</CardTitle>
              </CardHeader>
              <dl className="flex flex-col">
                {REQUIREMENTS.map((req) => (
                  <div key={req.title} className="border-t border-hairline py-4 first:border-t-0 first:pt-0">
                    <dt className="font-semibold">{req.title}</dt>
                    <dd className="mt-1 text-[0.9375rem] leading-relaxed text-muted-foreground">{req.body}</dd>
                  </div>
                ))}
              </dl>
            </Card>
          </Reveal>
        </div>
      </div>
    </section>
  );
}
