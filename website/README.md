# GlyphLens website

A static one-page site (`index.html`, `styles.css`, `app.js`, `theme.js`, `assets/`). No build step and no dependencies; fonts come from Google Fonts.
`vercel.json` sets a strict Content-Security-Policy and cache headers (assets: one hour, revalidated, because the file names are not fingerprinted).

**Live (GitHub Pages):** https://ojeshfr.github.io/glyphlens/ is served from the `gh-pages` branch of the repository, which holds **only** these
site files (no app code): `index.html`, `styles.css`, `app.js`, `theme.js`, `.nojekyll`, `assets/*.avif` and `assets/icon.svg`. All paths in the page are
relative because the site lives under `/glyphlens/`. GitHub Pages cannot send headers, so the Content-Security-Policy is also set in a `<meta>` tag in
`index.html`. To update it, copy the changed files into a checkout of `gh-pages`, commit and push; Pages rebuilds in about a minute.
**Other hosts:** `vercel.json` carries the same policy as real headers (a Vercel deployment was tried and refused by the connector, see
IMPLEMENTATION-STATUS.md). **Preview locally:** `python -m http.server 8765` inside this folder (it sends no headers, so the `<meta>` policy is what gets tested).

**Before it goes live, check:**
- The page states there is no installer yet and no distribution licence ([LICENSE-CHOICE.md](../LICENSE-CHOICE.md)). Update the "Where it stands"
  section and the download button when a release exists.
- Screenshots in `assets/` are renders of the real app with sample data; refresh them when the UI changes. The page uses the small `.avif` files
  (49 KB for all six); the `.png` files are the lossless masters and are not referenced.
- It names no person, company, contact address or private repository.
