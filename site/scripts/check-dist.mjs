// Checks the production build in dist/ for the rules the site must keep (run by `npm run build` and `npm run check`):
// the files GitHub Pages needs, a strict Content-Security-Policy, relative URLs only, and no reference to another origin
// other than the plain links a visitor can click (GitHub). Exits with 1 and a list of problems if anything is wrong.
import { readFileSync, readdirSync, statSync, existsSync } from "node:fs";
import { join, relative, extname } from "node:path";
import { fileURLToPath } from "node:url";

const dist = fileURLToPath(new URL("../dist", import.meta.url));
const problems = [];

for (const required of ["index.html", "404.html", ".nojekyll", "robots.txt", "theme-init.js", "icon.svg"]) {
  if (!existsSync(join(dist, required))) problems.push(`missing ${required}`);
}

const html = existsSync(join(dist, "index.html")) ? readFileSync(join(dist, "index.html"), "utf8") : "";
const csp = /<meta http-equiv="Content-Security-Policy" content="([^"]+)"/.exec(html)?.[1] ?? "";
for (const directive of ["default-src 'self'", "script-src 'self'", "img-src 'self' data:", "connect-src 'none'", "base-uri 'none'", "form-action 'none'"]) {
  if (!csp.includes(directive)) problems.push(`CSP lacks "${directive}"`);
}
if (/unsafe-eval|unsafe-inline/.test(csp)) problems.push("CSP allows unsafe-eval or unsafe-inline");
if (!html.includes('<meta name="referrer" content="no-referrer"')) problems.push("missing referrer meta");
if (/<script(?![^>]*\bsrc=)[^>]*>/.test(html)) problems.push("index.html contains an inline script");
if (/<style[\s>]/.test(html) || /\sstyle="/.test(html)) problems.push("index.html contains inline styles");
for (const [, url] of html.matchAll(/(?:src|href)="([^"]+)"/g)) {
  if (url.startsWith("/") || /^https?:/.test(url)) {
    if (url !== "https://github.com/ojeshfr/glyphlens") problems.push(`index.html references a non-relative URL: ${url}`);
  }
}

// Third-party hosts may appear only as plain links (GitHub). Anything that would be fetched (fonts, scripts, images) must be local.
const ALLOWED_LINK_HOSTS = new Set(["github.com"]);
// Licence banners and XML namespaces mention URLs that are never fetched.
const KNOWN_NON_FETCH = [/tailwindcss\.com \*\//,/www\.w3\.org\/(2000|1999|1998)\//, /reactjs\.org\/docs\/error-decoder/, /react\.dev\/errors/];
function walk(dir) {
  for (const name of readdirSync(dir)) {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) walk(path);
    else if ([".js", ".css", ".html"].includes(extname(name))) {
      const text = readFileSync(path, "utf8");
      if (extname(name) === ".css" && /url\((?!["']?(?:\.|data:))/i.test(text.replace(/url\(["']?[\w./-]+\.(woff2?|avif|webp|svg)["']?\)/g, ""))) {
        problems.push(`${relative(dist, path)}: CSS url() that is not relative`);
      }
      for (const [match, host] of text.matchAll(/https?:\/\/([a-z0-9.-]+)/gi)) {
        if (KNOWN_NON_FETCH.some((re) => re.test(text.slice(text.indexOf(match), text.indexOf(match) + 80)))) continue;
        if (!ALLOWED_LINK_HOSTS.has(host.toLowerCase())) problems.push(`${relative(dist, path)}: mentions ${match}`);
      }
    }
  }
}
if (existsSync(dist)) walk(dist);

if (problems.length) {
  console.error(`check-dist: ${problems.length} problem(s)\n - ${[...new Set(problems)].join("\n - ")}`);
  process.exit(1);
}
console.log("check-dist: OK (required files, CSP, relative URLs, no third-party fetch targets)");
