"""Builds the GlyphLens application icon (src/GlyphLens.App/Assets/GlyphLens.ico, sizes 16-256).

Usage (Pillow is in the development environment):
    worker\\.venv\\Scripts\\python.exe -I tools\\make-icon.py [SOURCE.png] [--output PATH]

SOURCE is the chosen logo as a square PNG of at least 1024x1024 pixels (render an SVG to PNG first). Each icon size is scaled
down from it on its own. Without SOURCE the current placeholder is drawn: the brand mark of website/assets/icon.svg (a blue
#0066CC rounded square with a white geometric "G", drawn as shapes so no font is involved).
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from PIL import Image, ImageDraw

SIZES = (16, 24, 32, 48, 64, 128, 256)
MIN_SOURCE = 1024
BLUE = (0x00, 0x66, 0xCC, 255)
WHITE = (255, 255, 255, 255)


def placeholder(side: int = MIN_SOURCE) -> Image.Image:
    image = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    pen = ImageDraw.Draw(image)
    pen.rounded_rectangle((0, 0, side - 1, side - 1), radius=round(side * 15 / 64), fill=BLUE)
    stroke, outer, centre = side * 0.13, side * 0.30, side / 2
    pen.arc((centre - outer, centre - outer, centre + outer, centre + outer), start=0, end=315, fill=WHITE, width=round(stroke))
    pen.rectangle((centre - stroke * 0.05, centre - stroke * 0.5, centre + outer, centre + stroke * 0.5), fill=WHITE)
    return image


def load_source(path: Path) -> Image.Image:
    with Image.open(path) as opened:
        image = opened.convert("RGBA")
    if image.width != image.height:
        raise SystemExit(f"{path.name} is {image.width}x{image.height}; the logo must be square.")
    if image.width < MIN_SOURCE:
        raise SystemExit(f"{path.name} is {image.width}x{image.height}; at least {MIN_SOURCE}x{MIN_SOURCE} is needed.")
    return image


def main() -> int:
    root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description="Build GlyphLens.ico from a square logo PNG (or the placeholder).")
    parser.add_argument("source", nargs="?", type=Path, help="square PNG, at least 1024x1024 (default: the placeholder mark)")
    parser.add_argument("--output", type=Path, default=root / "src" / "GlyphLens.App" / "Assets" / "GlyphLens.ico")
    args = parser.parse_args()

    source = load_source(args.source) if args.source else placeholder()
    images = [source.resize((size, size), Image.Resampling.LANCZOS) for size in SIZES]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    images[-1].save(args.output, format="ICO", sizes=[(s, s) for s in SIZES], append_images=images[:-1])

    with Image.open(args.output) as check:
        found = sorted(check.info.get("sizes", set()))
    if found != sorted((s, s) for s in SIZES):
        print(f"unexpected sizes in {args.output.name}: {found}", file=sys.stderr)
        return 1
    print(f"wrote {args.output} ({args.output.stat().st_size} bytes, sizes {', '.join(str(s) for s in SIZES)})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
