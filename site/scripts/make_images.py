r"""Turns the lossless app renders in ../website/assets into the optimised images used by the site.

Run from the site folder with the worker's Python (Pillow with AVIF and WebP support):
    ..\worker\.venv\Scripts\python.exe -I scripts\make_images.py
Writes src/assets/renders/<name>-<width>.avif and .webp. The PNG masters are never changed.
"""
import pathlib
import sys

from PIL import Image, ImageDraw

HERE = pathlib.Path(__file__).resolve().parent
SITE = HERE.parent
SOURCE = SITE.parent / "website" / "assets"
OUT = SITE / "src" / "assets" / "renders"

# name -> output widths (CSS pixels at 1x and 2x)
TARGETS = {
    "main-Capture": (740, 1479),
    "main-Settings": (740, 1479),
    "overlay": (285, 569),
}

# The overlay render has 15 px of desktop backdrop around the panel; cut it away and round the corners
# (14 DIP at 150 % = 21 px) so the panel sits on the page itself.
OVERLAY_BOX = (15, 15, 584, 824)
OVERLAY_RADIUS = 21


def prepare(name: str, master: Image.Image) -> Image.Image:
    if name != "overlay":
        return master
    panel = master.crop(OVERLAY_BOX)
    mask = Image.new("L", panel.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, panel.width - 1, panel.height - 1), OVERLAY_RADIUS, fill=255)
    panel.putalpha(mask)
    return panel


def main() -> int:
    OUT.mkdir(parents=True, exist_ok=True)
    total = 0
    for theme in ("light", "dark"):
        for name, widths in TARGETS.items():
            src = SOURCE / f"{theme}-{name}.png"
            if not src.exists():
                print(f"missing master: {src.name}", file=sys.stderr)
                return 1
            master = prepare(name, Image.open(src).convert("RGBA"))
            for width in widths:
                height = round(master.height * width / master.width)
                img = master if width == master.width else master.resize((width, height), Image.LANCZOS)
                stem = OUT / f"{theme}-{name}-{width}"
                img.save(stem.with_suffix(".avif"), "AVIF", quality=62, speed=4)
                img.save(stem.with_suffix(".webp"), "WEBP", quality=82, method=6)
                for ext in (".avif", ".webp"):
                    size = stem.with_suffix(ext).stat().st_size
                    total += size
                    print(f"{stem.name}{ext}: {img.width}x{img.height}, {size / 1024:.1f} KiB")
    print(f"total written: {total / 1024:.1f} KiB")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
