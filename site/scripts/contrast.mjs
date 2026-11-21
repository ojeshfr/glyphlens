// Computes WCAG 2.2 contrast ratios for every text/background token pair the components use, in both palettes,
// reading the values straight from src/index.css. Text needs 4.5:1; focus rings and icons that carry meaning need 3:1.
// Run: node scripts/contrast.mjs   (exits with 1 if any pair fails)
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const css = readFileSync(fileURLToPath(new URL("../src/index.css", import.meta.url)), "utf8");

function block(selector) {
  const start = css.indexOf(`${selector} {`);
  if (start < 0) throw new Error(`no ${selector} block`);
  const body = css.slice(start, css.indexOf("}", start));
  return Object.fromEntries([...body.matchAll(/--([\w-]+):\s*(#[0-9a-f]{6})/gi)].map(([, k, v]) => [k, v.toLowerCase()]));
}

const palettes = { light: block(":root"), dark: block(":root.dark") };

function luminance(hex) {
  const [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255).map((c) => (c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}
function ratio(a, b) {
  const [l1, l2] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (l1 + 0.05) / (l2 + 0.05);
}

const TEXT = 4.5;
const NON_TEXT = 3;
const pairs = [
  ...["background", "band", "card", "card-raised", "code", "nav-solid", "warning-surface"].map((bg) => ["foreground", bg, TEXT]),
  ...["background", "band", "card", "card-raised", "secondary", "nav-solid"].map((bg) => ["muted-foreground", bg, TEXT]),
  ...["background", "band", "card", "card-raised"].map((bg) => ["link", bg, TEXT]),
  ["primary-foreground", "primary", TEXT],
  ["primary-foreground", "primary-hover", TEXT],
  ["secondary-foreground", "secondary", TEXT],
  ["secondary-foreground", "secondary-hover", TEXT],
  ["popover-foreground", "popover", TEXT],
  ["warning", "warning-surface", TEXT],
  ["warning", "card-raised", NON_TEXT],
  ["success", "card-raised", NON_TEXT],
  ["success", "secondary", NON_TEXT],
  ["ring", "background", NON_TEXT],
  ["ring", "band", NON_TEXT],
  ["ring", "card", NON_TEXT],
  ["primary", "background", NON_TEXT],
];

let failures = 0;
for (const [name, p] of Object.entries(palettes)) {
  // In dark mode the active segment of a segmented control is drawn on --input.
  const extra = name === "dark" ? [["foreground", "input", TEXT]] : [];
  console.log(`\n${name}`);
  for (const [fg, bg, min] of [...pairs, ...extra]) {
    if (!p[fg] || !p[bg]) throw new Error(`${name}: missing token ${p[fg] ? bg : fg}`);
    const r = ratio(p[fg], p[bg]);
    const ok = r >= min;
    if (!ok) failures += 1;
    console.log(`${ok ? "PASS" : "FAIL"}  ${r.toFixed(2).padStart(5)}:1  (needs ${min})  ${fg} ${p[fg]} on ${bg} ${p[bg]}`);
  }
}
console.log(failures ? `\n${failures} pair(s) below the target` : "\nall pairs meet the target");
process.exit(failures ? 1 : 0);
