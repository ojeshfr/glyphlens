# GlyphLens website (`site/`)

The public product page for GlyphLens: one static page, built with Vite, React, TypeScript, Tailwind CSS v4, shadcn/ui
components (copied into `src/components/ui`, on Radix primitives), `lucide-react` icons and `motion` for the scroll reveals.
It is meant to be served from `https://ojeshfr.github.io/glyphlens/` (GitHub Pages, `gh-pages` branch, sub-path `/glyphlens/`).

The older hand-written page in `../website/` is separate and is not touched by anything here.

Rules the site keeps (checked by `npm test`):

- No third-party requests: fonts (Inter, from `@fontsource-variable/inter`), images and scripts are all served from the site
  itself; no analytics, no CDN, no cookies. Chinese text uses the visitor's system CJK fonts.
- A strict Content-Security-Policy in a `<meta>` tag (added to the production build by `vite.config.ts`):
  `default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'none'; base-uri 'none'; form-action 'none'`
  and more. No inline script or style is needed; the light/dark choice is applied before the first paint by `public/theme-init.js`.
- Relative URLs only (`base: "./"`) and hash links only, so the build works under any sub-path.
- Every statement on the page must be traceable to a document in this repository or a recorded test result.

## Requirements

Node.js 24 (tested with 24.19.0) and npm 11. Every dependency is pinned to an exact version in `package.json` and
`package-lock.json` (`.npmrc` sets `save-exact=true`).

## Install, run, build, test

```powershell
cd site
npm ci                 # install exactly what package-lock.json lists
npm run dev            # development server on http://localhost:5173 (no CSP in dev: Vite injects inline code for hot reload)
npm run build          # typecheck, production build into dist/, then scripts/check-dist.mjs
npm test               # typecheck + lint + contrast check + build + dist check
```

Individual checks:

| Command | What it checks |
|---|---|
| `npm run typecheck` | TypeScript (`tsc -b --noEmit`, strict) |
| `npm run lint` | ESLint with typescript-eslint and the React hooks rules |
| `npm run contrast` | WCAG contrast of every text/background token pair in both palettes, read from `src/index.css` (4.5:1 text, 3:1 rings and icons) |
| `npm run check` | `dist/` has `index.html`, `404.html`, `.nojekyll`, `robots.txt`; the CSP is present and strict; no inline script or style; no absolute or third-party URL that would be fetched |
| `npm audit` | Known vulnerabilities in the dependency tree |

To look at the production build locally, serve `dist/` with any static server, for example
`python -m http.server 8766 --bind 127.0.0.1 --directory dist` (the `site` entry in `.claude/launch.json` does this).
To check the sub-path, copy `dist` to a folder named `glyphlens` and serve its parent: the page is then at `/glyphlens/`.

## Updating the Download section (`src/release.ts`)

The Download section is driven by one typed value in `src/release.ts`.

- `{ published: false }` (the current state): an honest "not published yet" card that links to the repository's Releases page.
- When the first build is published, replace it with the real values:

```ts
export const release: Release = {
  published: true,
  version: "0.2.0",
  fileName: "GlyphLens-0.2.0-setup.exe",
  url: "https://github.com/ojeshfr/glyphlens/releases/download/v0.2.0/GlyphLens-0.2.0-setup.exe",
  sizeBytes: 0,      // (Get-Item .\GlyphLens-0.2.0-setup.exe).Length
  sha256: "",        // (Get-FileHash -Algorithm SHA256 .\GlyphLens-0.2.0-setup.exe).Hash.ToLower()
  publishedOn: "YYYY-MM-DD",
  notesUrl: "https://github.com/ojeshfr/glyphlens/releases/tag/v0.2.0",
  notes: "One or two sentences about this build.",
};
```

(The values above are an example of the shape only.) Take the size and the SHA-256 from the exact file you upload; visitors
will verify their download against them. The page then shows a Download button, the size and date, the SHA-256 with a copy
button, the `Get-FileHash` command and a note that Windows SmartScreen will warn because the build is not code-signed.
If the update feed or the signing status changes, update the Privacy section and the FAQ in the same change.

## Refreshing the screenshots

The images are real renders of the app. Their lossless masters live in `../website/assets/`
(`light-` and `dark-` `main-Capture.png`, `main-Settings.png`, `overlay.png`). After replacing a master with a new render of the
same size, regenerate the optimised AVIF and WebP files (Pillow with AVIF support; the worker's virtual environment has it):

```powershell
..\worker\.venv\Scripts\python.exe -I scripts\make_images.py
```

It writes `src/assets/renders/<theme>-<name>-<width>.{avif,webp}` (main window at 740 and 1479 px, the overlay cropped to the
panel at 285 and 569 px). If a master changes size, update the widths in `scripts/make_images.py` and the sizes in
`src/lib/renders.ts`, and update the alt texts in `src/sections/` if what the render shows has changed. Renders that contain test
data (for example the update notice in the Settings render) must say so on the page.

## Deploying to GitHub Pages (`gh-pages` branch)

The live site is the content of the `gh-pages` branch, served under `/glyphlens/`. Deploying replaces what is there.
Do it only when the owner has decided to publish this site in place of the current page.

```powershell
cd site
npm ci
npm test                                   # must pass
# Publish dist/ as the whole content of the gh-pages branch, from a separate worktree:
git fetch origin gh-pages
git worktree add ..\..\gh-pages-deploy gh-pages
Remove-Item -Recurse -Force ..\..\gh-pages-deploy\* -Exclude .git
Copy-Item -Recurse -Force dist\* ..\..\gh-pages-deploy\
Copy-Item -Force dist\.nojekyll ..\..\gh-pages-deploy\
cd ..\..\gh-pages-deploy
git add -A
git commit -m "Deploy site"
git push origin gh-pages
cd ..\GlyphLens
git worktree remove ..\gh-pages-deploy
```

After the push, check `https://ojeshfr.github.io/glyphlens/`: the page loads, the browser's network list shows only
`ojeshfr.github.io` requests and the console shows no Content-Security-Policy error. GitHub Pages serves `404.html` for unknown
paths; its link points at `/glyphlens/`.

## Layout

```
site/
├── index.html                 page shell (no inline code)
├── public/                    copied as-is: theme-init.js, icon.svg, 404.html, robots.txt, .nojekyll
├── scripts/                   check-dist.mjs, contrast.mjs, make_images.py
├── src/
│   ├── release.ts             Download section state (the only file to edit for a release)
│   ├── index.css              Tailwind and the light/dark design tokens
│   ├── App.tsx, main.tsx
│   ├── components/ui/         shadcn/ui components (button, badge, card, tabs, toggle-group, accordion, tooltip, sheet, separator, kbd, copy-button)
│   ├── components/            render-image (AVIF/WebP picture), reveal (motion, respects reduced motion)
│   ├── lib/                   theme (System/Light/Dark, remembered), renders, utils
│   ├── sections/              nav, hero, how-it-works, the-app, languages, privacy, download, faq, footer
│   └── assets/renders/        optimised app renders (generated)
└── vite.config.ts             base "./", CSP meta for builds
```

Note on the mobile menu: Radix's `DialogOverlay` injects a `<style>` element at run time (through `react-remove-scroll`), which
`style-src 'self'` blocks. `src/components/ui/sheet.tsx` therefore draws its own backdrop and locks scrolling with a class on
`<html>`; focus trapping, Escape and `aria-hidden` on the rest of the page still come from Radix.
