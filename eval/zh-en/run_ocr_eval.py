"""Chinese OCR benchmark: PaddleOCR PP-OCRv5 (mobile and server models) on real screen captures.

Input: the PNG files and ``truth.json`` produced by ``render-scenes.ps1 -Mode Capture`` (real Windows windows captured with
PrintWindow). For every image the recognised lines are put in reading order and compared with the text that was drawn:

* **CER** - character error rate of the whole image: Levenshtein distance between the concatenated, whitespace-free
  reading-ordered text and the truth, divided by the truth length. Lower is better; it also counts missing and extra lines.
* **line recall** - the share of truth lines that were found as a recognised line with a per-line CER of at most 0.1.

Latency is the median of 3 timed runs after a warm-up run. Nothing is invented: the numbers are printed and written to
``results/ocr-eval.json``. PaddleOCR downloads its model files the first time a model is used (a one-off, explicit research
step); point ``PADDLE_PDX_CACHE_HOME`` at a directory outside the repository.

    set PADDLE_PDX_CACHE_HOME=D:\\...\\paddlex-cache
    python eval\\zh-en\\run_ocr_eval.py --shots D:\\...\\screenshots --models mobile server
"""

from __future__ import annotations

import argparse
import ctypes
import json
import statistics
import sys
import time
from ctypes import wintypes
from pathlib import Path

THREADS = 4
VARIANTS = {
    "mobile": ("PP-OCRv5_mobile_det", "PP-OCRv5_mobile_rec"),
    "server": ("PP-OCRv5_server_det", "PP-OCRv5_server_rec"),
}


def working_set_mb() -> float:
    class Counters(ctypes.Structure):
        _fields_ = [
            ("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD), ("PeakWorkingSetSize", ctypes.c_size_t),
            ("WorkingSetSize", ctypes.c_size_t), ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPagedPoolUsage", ctypes.c_size_t), ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
            ("QuotaNonPagedPoolUsage", ctypes.c_size_t), ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t),
        ]

    kernel32, psapi = ctypes.WinDLL("kernel32"), ctypes.WinDLL("psapi")
    kernel32.GetCurrentProcess.restype = wintypes.HANDLE
    psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(Counters), wintypes.DWORD]
    psapi.GetProcessMemoryInfo.restype = wintypes.BOOL
    counters = Counters()
    counters.cb = ctypes.sizeof(Counters)
    psapi.GetProcessMemoryInfo(kernel32.GetCurrentProcess(), ctypes.byref(counters), counters.cb)
    return counters.WorkingSetSize / (1024 * 1024)


def levenshtein(a: str, b: str) -> int:
    if not a:
        return len(b)
    previous = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        current = [i]
        for j, cb in enumerate(b, 1):
            current.append(min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + (ca != cb)))
        previous = current
    return previous[-1]


def squash(text: str) -> str:
    return "".join(text.split())


def reading_order(boxes: list[tuple[float, float, float, float, str, float]]) -> list[tuple[float, float, float, float, str, float]]:
    """Top to bottom; boxes whose vertical centres are within half a line height are one row, left to right."""
    ordered = sorted(boxes, key=lambda b: (b[1] + b[3]) / 2)
    rows: list[list[tuple[float, float, float, float, str, float]]] = []
    for box in ordered:
        centre = (box[1] + box[3]) / 2
        height = box[3] - box[1]
        if rows and abs(centre - (rows[-1][0][1] + rows[-1][0][3]) / 2) <= height / 2:
            rows[-1].append(box)
        else:
            rows.append([box])
    return [box for row in rows for box in sorted(row, key=lambda b: b[0])]


def recognise(ocr, path: Path) -> list[tuple[float, float, float, float, str, float]]:
    result = ocr.predict(str(path))[0]
    data = result.json["res"] if hasattr(result, "json") else dict(result)
    boxes = []
    for text, score, poly in zip(data["rec_texts"], data["rec_scores"], data["rec_polys"]):
        xs, ys = [p[0] for p in poly], [p[1] for p in poly]
        boxes.append((min(xs), min(ys), max(xs), max(ys), text, float(score)))
    return boxes


def score_image(truth_lines: list[str], boxes: list[tuple[float, float, float, float, str, float]]) -> dict:
    ordered = reading_order(boxes)
    predicted_lines = [b[4] for b in ordered]
    truth_text = squash("".join(truth_lines))
    predicted_text = squash("".join(predicted_lines))
    cer = levenshtein(predicted_text, truth_text) / max(len(truth_text), 1)
    found = 0
    for line in truth_lines:
        target = squash(line)
        best = min((levenshtein(squash(p), target) / max(len(target), 1) for p in predicted_lines), default=1.0)
        found += best <= 0.1
    return {
        "cer": round(cer, 4),
        "line_recall": round(found / max(len(truth_lines), 1), 3),
        "truth_chars": len(truth_text),
        "predicted_lines": predicted_lines,
        "mean_recognition_score": round(statistics.mean(b[5] for b in boxes), 3) if boxes else None,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--shots", required=True, type=Path)
    parser.add_argument("--models", nargs="+", default=["mobile", "server"], choices=sorted(VARIANTS))
    parser.add_argument("--out", type=Path, default=Path(__file__).with_name("results"))
    args = parser.parse_args()

    truth = json.loads((args.shots / "truth.json").read_text(encoding="utf-8-sig"))
    args.out.mkdir(parents=True, exist_ok=True)
    report: dict = {"python": sys.version.split()[0], "engines": {}}

    for variant in args.models:
        det, rec = VARIANTS[variant]
        before = working_set_mb()
        t0 = time.perf_counter()
        from paddleocr import PaddleOCR  # imported late: it is slow and noisy

        ocr = PaddleOCR(
            text_detection_model_name=det,
            text_recognition_model_name=rec,
            use_doc_orientation_classify=False,
            use_doc_unwarping=False,
            use_textline_orientation=False,
            device="cpu",
            # ONNX Runtime with 4 threads is the fastest CPU path measured here (Paddle's own engine: ~5.4 s per image,
            # ONNX with 20 threads: ~2.9 s, with 4 threads: ~0.85 s; more threads are slower on this hybrid CPU).
            engine="onnxruntime",
            engine_config={"intra_op_num_threads": THREADS, "graph_optimization_level": 99},
        )
        init_s = time.perf_counter() - t0
        entry: dict = {"models": [det, rec], "init_s": round(init_s, 1), "scenes": {}}
        for name, spec in truth.items():
            image = args.shots / spec["file"]
            boxes = recognise(ocr, image)  # warm-up (first call compiles / allocates)
            timings = []
            for _ in range(3):
                t1 = time.perf_counter()
                boxes = recognise(ocr, image)
                timings.append((time.perf_counter() - t1) * 1000)
            scored = score_image(spec["lines"], boxes)
            scored.update({"category": spec["category"], "script": spec["script"], "median_ms": round(statistics.median(timings), 1)})
            entry["scenes"][name] = scored
            print(f"{variant:6} {name:22} CER {scored['cer']:.3f}  line recall {scored['line_recall']:.2f}  {scored['median_ms']:.0f} ms")
        scenes = entry["scenes"].values()
        total_chars = sum(s["truth_chars"] for s in scenes)
        entry["overall_cer_weighted"] = round(sum(s["cer"] * s["truth_chars"] for s in scenes) / total_chars, 4)
        entry["median_ms_per_image"] = round(statistics.median(s["median_ms"] for s in scenes), 1)
        entry["memory_mb_after"] = round(working_set_mb(), 1)
        entry["memory_mb_growth"] = round(working_set_mb() - before, 1)
        report["engines"][variant] = entry
        print(f"{variant}: overall CER {entry['overall_cer_weighted']}, median {entry['median_ms_per_image']} ms/image, init {entry['init_s']} s, memory {entry['memory_mb_after']} MB")
        del ocr

    (args.out / "ocr-eval.json").write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
