// Writes dist/THIRD-PARTY-LICENSES.txt: the name, version, licence identifier and full licence text of every package that ships in the
// site (the production dependency tree). Minified bundles do not keep licence notices, and MIT, ISC and OFL require them to travel
// with copies, so the build emits them as a separate file that the footer links to.
import { execFileSync } from "node:child_process";
import { existsSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";

const root = resolve(import.meta.dirname, "..");
const listing = execFileSync("npm", ["ls", "--omit=dev", "--all", "--parseable"], { cwd: root, shell: true, encoding: "utf8" });
const dirs = [...new Set(listing.split(/\r?\n/).map((l) => l.trim()).filter(Boolean))].filter((d) => resolve(d) !== root);

const seen = new Map();
for (const dir of dirs) {
  const pkgPath = join(dir, "package.json");
  if (!existsSync(pkgPath)) continue;
  const pkg = JSON.parse(readFileSync(pkgPath, "utf8"));
  const key = `${pkg.name}@${pkg.version}`;
  if (seen.has(key)) continue;
  const file = readdirSync(dir).find((f) => /^(licen[cs]e|copying)(\.(md|txt))?$/i.test(f));
  const text = file ? readFileSync(join(dir, file), "utf8").trim() : null;
  const license = typeof pkg.license === "string" ? pkg.license : pkg.license?.type ?? "see package";
  seen.set(key, { name: pkg.name, version: pkg.version, license, text });
}

const entries = [...seen.values()].sort((a, b) => a.name.localeCompare(b.name));
const missing = entries.filter((e) => !e.text).map((e) => `${e.name}@${e.version}`);
const out = [
  "Third-party software included in this website (production dependencies, with their licences).",
  "The GlyphLens site itself is licensed under the Apache License 2.0 (https://github.com/ojeshfr/glyphlens).",
  "",
  ...entries.flatMap((e) => [
    "=".repeat(78),
    `${e.name} ${e.version}  -  ${e.license}`,
    "=".repeat(78),
    e.text ?? `(No licence file in the package; licence declared in package.json: ${e.license})`,
    "",
  ]),
].join("\n");

writeFileSync(join(root, "dist", "THIRD-PARTY-LICENSES.txt"), out, "utf8");
console.log(`licenses: ${entries.length} packages written to dist/THIRD-PARTY-LICENSES.txt` + (missing.length ? ` (no licence file: ${missing.join(", ")})` : ""));
