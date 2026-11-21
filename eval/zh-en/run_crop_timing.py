"""Measures how OCR time depends on the amount of image, to decide whether re-reading only a changed part of a frame pays off.

For each screenshot it times the worker's real recogniser (PP-OCRv5 mobile on ONNX Runtime, the one the app uses) on the whole image
and on crops of it: the bottom third (a new chat message appears at the bottom), a band the height of one line, and a tight box around
one text line. Each size is run a few times after a warm-up and the median is reported. Nothing is written except the JSON result.

Usage (from the repository root):
    worker\\.venv\\Scripts\\python.exe eval\\zh-en\\run_crop_timing.py --shots <screenshots dir> --models <models dir with ocr\\...> --out <result.json>
"""

from __future__ import annotations

import argparse
import io
import json
import statistics
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "worker"))

from glyphlens_worker.engines.ocr.backend import (  # noqa: E402
    DETECTION_DIRECTORY,
    RECOGNITION_DIRECTORY,
    PaddleOnnxRuntimeLoader,
)


def _png(image) -> bytes:
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    return buffer.getvalue()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--shots", required=True, type=Path)
    parser.add_argument("--models", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--repeats", type=int, default=5)
    args = parser.parse_args()

    from PIL import Image  # installed with paddleocr

    loader = PaddleOnnxRuntimeLoader()
    engine = loader.load(
        args.models / "ocr" / DETECTION_DIRECTORY,
        args.models / "ocr" / RECOGNITION_DIRECTORY,
        args.out.parent,
    )

    def time_ms(png: bytes) -> tuple[float, int]:
        samples = []
        lines = 0
        for _ in range(args.repeats):
            started = time.perf_counter()
            output = engine.recognise(png)
            samples.append((time.perf_counter() - started) * 1000)
            lines = len(output.lines)
        return statistics.median(samples), lines

    results = {}
    for name in ("chat-light", "chat-dark-small", "document-paragraphs", "settings-ui"):
        path = args.shots / f"{name}.png"
        if not path.exists():
            continue
        image = Image.open(path).convert("RGB")
        w, h = image.size
        full = _png(image)
        engine.recognise(full)  # warm-up

        crops = {
            "full frame": image,
            "bottom third": image.crop((0, h * 2 // 3, w, h)),
            "one-line band (full width)": image.crop((0, h * 2 // 3, w, h * 2 // 3 + 60)),
            "tight box around one line": image.crop((20, h * 2 // 3 + 10, w // 2, h * 2 // 3 + 50)),
        }
        rows = {}
        for label, crop in crops.items():
            milliseconds, lines = time_ms(_png(crop))
            rows[label] = {"size": list(crop.size), "median_ms": round(milliseconds, 1), "lines": lines}
        results[name] = {"frame": [w, h], "crops": rows}
        print(name, json.dumps(rows))

    args.out.write_text(json.dumps(results, indent=2), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
