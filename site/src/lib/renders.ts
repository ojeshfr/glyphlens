// Real renders of the GlyphLens app (made from the PNG masters in website/assets by scripts/make_images.py).
const files = import.meta.glob<string>("../assets/renders/*.{avif,webp}", { eager: true, query: "?url", import: "default" });

export type RenderName = "overlay" | "main-Capture" | "main-Settings";
export type RenderTheme = "light" | "dark";

/** Intrinsic size of the largest file and the widths written by make_images.py. */
const META: Record<RenderName, { width: number; height: number; widths: readonly [number, number] }> = {
  overlay: { width: 569, height: 809, widths: [285, 569] },
  "main-Capture": { width: 1479, height: 984, widths: [740, 1479] },
  "main-Settings": { width: 1479, height: 984, widths: [740, 1479] },
};

function url(theme: RenderTheme, name: RenderName, width: number, ext: "avif" | "webp"): string {
  const key = `../assets/renders/${theme}-${name}-${width}.${ext}`;
  const value = files[key];
  if (!value) throw new Error(`missing render ${key}`);
  return value;
}

export function renderSources(theme: RenderTheme, name: RenderName) {
  const meta = META[name];
  const srcSet = (ext: "avif" | "webp") => meta.widths.map((w) => `${url(theme, name, w, ext)} ${w}w`).join(", ");
  return {
    avif: srcSet("avif"),
    webp: srcSet("webp"),
    fallback: url(theme, name, meta.widths[0], "webp"),
    width: meta.width,
    height: meta.height,
  };
}
